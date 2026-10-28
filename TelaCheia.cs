using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MrsChat;

/// <summary>
/// Interface em tela cheia, desenhada com sequências ANSI (sem bibliotecas externas):
///
///   ■ cabeçalho: app, versão, sala, endereço, conexão e hora
///   ╔═[ conversa ]════════════════════════════╦═[ integrantes ]═══════╗
///   ║ histórico com rolagem (PgUp/PgDn)        ║ ▸ nomes                ║
///   ╠═[ mensagem ]═════════════════════════════╩════════════════════════╣
///   ║ NOME ▸ texto sendo digitado                                         ║
///   ╚═[ dicas de teclas ]═════════════════════════════════════════════════╝
///
/// Cores, moldura e textos vêm do <see cref="Tema"/>. A tela é montada numa grade de
/// células e só as linhas que mudaram são reenviadas ao terminal, então não pisca.
/// Usa a tela alternativa do terminal: ao sair, o conteúdo de antes volta como era.
/// </summary>
sealed class TelaCheia : IInterfaceChat
{
    public const int LarguraMinima = 40;
    public const int AlturaMinima = 10;
    private const int MaxMensagens = 2000;
    private const int MaxLinhasEntrada = 3;
    private const int Padrao = Tema.Padrao;
    private static readonly TimeSpan PassoAbertura = TimeSpan.FromMilliseconds(85);
    private static readonly TimeSpan PausaAbertura = TimeSpan.FromMilliseconds(550);

    private readonly record struct Celula(string? T, int Fg, int Bg, bool Negrito)
    {
        public static readonly Celula Vazia = new(" ", Padrao, Padrao, false);
    }

    private readonly record struct Glifo(string T, int L, int Fg, bool Negrito);

    private sealed class Mensagem(Trecho[] trechos)
    {
        public Trecho[] Trechos { get; } = trechos;
        public int LarguraCache = -1;
        public List<Celula[]> Linhas = [];
    }

    private readonly object _trava = new();
    private readonly Tema _tema;
    private readonly bool _cores;
    private readonly List<Mensagem> _mensagens = [];
    private readonly StringBuilder _entrada = new();
    private readonly List<string> _historico = [];
    private int _posHistorico;
    private int _cursor; // posição em _entrada (índice de char)

    private string _nome = "";
    private bool _mascarar;
    private string? _sala;
    private string _endereco = "";
    private bool _conectado;
    private List<string> _usuarios = [];
    private string _voce = "";
    private bool _listaCompleta = true;

    private int _rolagem;      // linhas acima do fim (0 = acompanhando as novas)
    private int _novasAbaixo;  // mensagens que chegaram enquanto rolado para cima
    private int _alturaChat = 1;
    private int _larguraTexto;
    private string? _aviso;

    private string? _completarPrefixo;
    private int _completarIndice;
    private int _completarInicio;

    private readonly Stopwatch _relogio = Stopwatch.StartNew();
    private bool _aberturaPulada;

    private Celula[,]? _anterior;
    private int _w, _h;
    private string _segundo = "";
    private bool _sujo = true;
    private bool _redesenharTudo = true;
    private readonly Thread _renderizador;
    private volatile bool _ativo = true;

    /// <summary>Se dá para usar a tela cheia neste terminal (senão, cai no modo simples).</summary>
    public static bool Disponivel(out string? motivo)
    {
        motivo = null;
        if (Console.IsInputRedirected || Console.IsOutputRedirected) { motivo = "entrada ou saída redirecionada"; return false; }
        if (Environment.GetEnvironmentVariable("TERM") == "dumb") { motivo = "TERM=dumb"; return false; }
        if (!Vt.Habilitar()) { motivo = "o terminal não suporta sequências ANSI"; return false; }
        return true;
    }

    public TelaCheia(Tema tema, bool cores)
    {
        _tema = tema;
        _cores = cores;
        Console.OutputEncoding = new UTF8Encoding(false);
        // tela alternativa, sem quebra automática de linha (evita rolar ao escrever no canto), cursor oculto
        Console.Out.Write("\x1b[?1049h\x1b[?7l\x1b[?25l" + _tema.EstiloCursor + "\x1b[2J");
        Console.Out.Flush();
        _renderizador = new Thread(LacoDeRenderizacao) { IsBackground = true, Name = "TelaCheia" };
        _renderizador.Start();
    }

    public void Dispose()
    {
        if (!_ativo) return;
        _ativo = false;
        _renderizador.Join(500);
        lock (_trava)
        {
            Console.Out.Write("\x1b[0m" + (_tema.EstiloCursor.Length > 0 ? "\x1b[0 q" : "") + "\x1b[?25h\x1b[?7h\x1b[?1049l");
            Console.Out.Flush();
        }
    }

    // ================================================================ API

    public void DefinirNome(string nome)
    {
        lock (_trava) { _nome = nome; _sujo = true; }
    }

    public bool Mascarar
    {
        get => _mascarar;
        set { lock (_trava) { _mascarar = value; _sujo = true; } }
    }

    public void Escrever(Evento evento)
    {
        lock (_trava)
        {
            var m = new Mensagem(_tema.Formatar(evento));
            _mensagens.Add(m);
            if (_mensagens.Count > MaxMensagens) _mensagens.RemoveRange(0, _mensagens.Count - MaxMensagens);

            // Se o usuário está lendo mensagens antigas, a vista não pode "andar" sozinha.
            if (_rolagem > 0 && _larguraTexto > 0)
            {
                _rolagem += LinhasDe(m, _larguraTexto).Count;
                _novasAbaixo++;
            }
            _sujo = true;
        }
    }

    public void Limpar()
    {
        lock (_trava)
        {
            _mensagens.Clear();
            _rolagem = 0;
            _novasAbaixo = 0;
            _sujo = true;
        }
    }

    public void DefinirSala(string? sala, string endereco)
    {
        lock (_trava) { _sala = sala; _endereco = endereco; _sujo = true; }
    }

    public void DefinirUsuarios(IReadOnlyCollection<string> nomes, string voce, bool listaCompleta)
    {
        lock (_trava)
        {
            _usuarios = nomes.Order(StringComparer.OrdinalIgnoreCase).ToList();
            _voce = voce;
            _listaCompleta = listaCompleta;
            _sujo = true;
        }
    }

    public void DefinirConectado(bool conectado)
    {
        lock (_trava) { _conectado = conectado; _sujo = true; }
    }

    public void AguardarTecla(string aviso, CancellationToken ct)
    {
        lock (_trava) { _aviso = aviso; _aberturaPulada = true; _sujo = true; }
        while (!ct.IsCancellationRequested)
        {
            if (TemTecla()) { Console.ReadKey(intercept: true); return; }
            Thread.Sleep(30);
        }
    }

    // ============================================================= entrada

    public string? LerLinha(CancellationToken ct)
    {
        while (true)
        {
            while (!TemTecla())
            {
                if (ct.IsCancellationRequested) return null;
                Thread.Sleep(15);
            }
            if (ct.IsCancellationRequested) return null;

            ConsoleKeyInfo k = Console.ReadKey(intercept: true);
            bool ctrl = k.Modifiers.HasFlag(ConsoleModifiers.Control);
            lock (_trava)
            {
                _sujo = true;
                if (EmAbertura())
                {
                    _aberturaPulada = true; // a primeira tecla só pula a abertura
                    continue;
                }
                if (k.Key != ConsoleKey.Tab) _completarPrefixo = null;

                switch (k.Key)
                {
                    case ConsoleKey.Enter:
                    {
                        string linha = _entrada.ToString();
                        if (!_mascarar && linha.Trim().Length > 0 && (_historico.Count == 0 || _historico[^1] != linha))
                            _historico.Add(linha);
                        _posHistorico = _historico.Count;
                        _entrada.Clear();
                        _cursor = 0;
                        // Enviar uma mensagem volta a vista para o fim da conversa.
                        if (!linha.StartsWith('/')) { _rolagem = 0; _novasAbaixo = 0; }
                        return linha;
                    }
                    case ConsoleKey.Backspace when ctrl:
                    case ConsoleKey.W when ctrl:
                        ApagarPalavra();
                        break;
                    case ConsoleKey.Backspace:
                        if (_cursor > 0)
                        {
                            int n = TamanhoAntes(_cursor);
                            _entrada.Remove(_cursor - n, n);
                            _cursor -= n;
                        }
                        break;
                    case ConsoleKey.Delete:
                        if (_cursor < _entrada.Length) _entrada.Remove(_cursor, TamanhoDepois(_cursor));
                        break;
                    case ConsoleKey.LeftArrow:
                        if (_cursor > 0) _cursor -= TamanhoAntes(_cursor);
                        break;
                    case ConsoleKey.RightArrow:
                        if (_cursor < _entrada.Length) _cursor += TamanhoDepois(_cursor);
                        break;
                    case ConsoleKey.Home:
                        _cursor = 0;
                        break;
                    case ConsoleKey.End:
                        _cursor = _entrada.Length;
                        break;
                    case ConsoleKey.UpArrow when !_mascarar:
                        if (_posHistorico > 0) TrocarEntrada(_historico[--_posHistorico]);
                        break;
                    case ConsoleKey.DownArrow when !_mascarar:
                        if (_posHistorico < _historico.Count - 1) TrocarEntrada(_historico[++_posHistorico]);
                        else { _posHistorico = _historico.Count; TrocarEntrada(""); }
                        break;
                    case ConsoleKey.PageUp:
                        _rolagem += Math.Max(1, _alturaChat - 1);
                        break;
                    case ConsoleKey.PageDown:
                        _rolagem = Math.Max(0, _rolagem - Math.Max(1, _alturaChat - 1));
                        if (_rolagem == 0) _novasAbaixo = 0;
                        break;
                    case ConsoleKey.Escape:
                        if (_entrada.Length > 0) { _entrada.Clear(); _cursor = 0; }
                        else { _rolagem = 0; _novasAbaixo = 0; }
                        break;
                    case ConsoleKey.Tab when !_mascarar:
                        CompletarNome();
                        break;
                    case ConsoleKey.L when ctrl:
                        _redesenharTudo = true;
                        break;
                    case ConsoleKey.U when ctrl:
                        _entrada.Remove(0, _cursor);
                        _cursor = 0;
                        break;
                    case ConsoleKey.D when ctrl && _entrada.Length == 0:
                        return null;
                    default:
                        if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar) && _entrada.Length < Protocolo.TamanhoMaximoLinha)
                        {
                            _entrada.Insert(_cursor, k.KeyChar);
                            _cursor++;
                        }
                        break;
                }
            }
        }
    }

    private void TrocarEntrada(string texto)
    {
        _entrada.Clear().Append(texto);
        _cursor = _entrada.Length;
    }

    private int TamanhoAntes(int i) =>
        i >= 2 && char.IsLowSurrogate(_entrada[i - 1]) && char.IsHighSurrogate(_entrada[i - 2]) ? 2 : 1;

    private int TamanhoDepois(int i) =>
        i + 1 < _entrada.Length && char.IsHighSurrogate(_entrada[i]) && char.IsLowSurrogate(_entrada[i + 1]) ? 2 : 1;

    private void ApagarPalavra()
    {
        int i = _cursor;
        while (i > 0 && _entrada[i - 1] == ' ') i--;
        while (i > 0 && _entrada[i - 1] != ' ') i--;
        _entrada.Remove(i, _cursor - i);
        _cursor = i;
    }

    /// <summary>Tab completa o nome de um integrante; Tab de novo passa para o próximo que combina.</summary>
    private void CompletarNome()
    {
        if (_completarPrefixo is null)
        {
            int ini = _cursor;
            while (ini > 0 && _entrada[ini - 1] != ' ') ini--;
            _completarInicio = ini;
            _completarPrefixo = _entrada.ToString(ini, _cursor - ini);
            _completarIndice = -1;
        }

        var opcoes = _usuarios.Where(u => u.StartsWith(_completarPrefixo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (opcoes.Count == 0) { _completarPrefixo = null; return; }

        _completarIndice = (_completarIndice + 1) % opcoes.Count;
        string nome = opcoes[_completarIndice];
        _entrada.Remove(_completarInicio, _cursor - _completarInicio);
        _entrada.Insert(_completarInicio, nome);
        _cursor = _completarInicio + nome.Length;
    }

    private bool TemTecla()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { return false; }
    }

    // ========================================================= renderização

    private void LacoDeRenderizacao()
    {
        while (_ativo)
        {
            try
            {
                lock (_trava)
                {
                    if (!_ativo) break;
                    int w = Math.Max(1, Console.WindowWidth), h = Math.Max(1, Console.WindowHeight);
                    if (w != _w || h != _h) { _w = w; _h = h; _redesenharTudo = true; _sujo = true; }
                    string segundo = DateTime.Now.ToString("HH:mm:ss");
                    if (segundo != _segundo) { _segundo = segundo; _sujo = true; }
                    if (EmAbertura()) _sujo = true;
                    if (_sujo || _redesenharTudo)
                    {
                        _sujo = false;
                        Renderizar();
                    }
                }
            }
            catch (IOException) { }
            Thread.Sleep(25);
        }
    }

    private Trecho[][]? _abertura;

    private bool EmAbertura()
    {
        if (_aberturaPulada) return false;
        _abertura = _tema.Abertura(_nome, _endereco); // refeita a cada quadro: nome e endereço chegam logo após abrir
        if (_abertura is null) return false;
        if (_relogio.Elapsed > PassoAbertura * _abertura.Length + PausaAbertura) { _aberturaPulada = true; return false; }
        return true;
    }

    private void Renderizar()
    {
        int W = _w, H = _h;
        var g = new Celula[H, W];
        for (int r = 0; r < H; r++)
            for (int c = 0; c < W; c++)
                g[r, c] = Celula.Vazia;

        (int linha, int coluna)? cursor = null;

        if (W < LarguraMinima || H < AlturaMinima)
        {
            string msg = _tema.JanelaPequena(LarguraMinima, AlturaMinima);
            Texto(g, H / 2, Math.Max(0, (W - LarguraTexto(msg)) / 2), W, Cortar(msg, W), _tema.CorTitulo, negrito: true);
        }
        else if (EmAbertura())
        {
            DesenharAbertura(g, W, H);
        }
        else
        {
            var m = _tema.Moldura;

            // ---- medidas
            bool comMembros = W >= 60;
            int larguraMembros = comMembros ? Math.Clamp(W / 4, 16, 28) : 0;
            int xDivisao = comMembros ? W - 2 - larguraMembros : W - 1;
            int larguraEntrada = W - 4;
            var (linhasEntrada, cursorEntrada) = MontarEntrada(larguraEntrada);
            int nEntrada = Math.Min(MaxLinhasEntrada, linhasEntrada.Count);
            int ySeparador = H - 2 - nEntrada;
            int yChat = 2;
            int alturaChat = ySeparador - yChat;
            _alturaChat = alturaChat;
            int xTexto = 2, larguraTexto = xDivisao - 1 - xTexto;
            _larguraTexto = larguraTexto;

            DesenharCabecalho(g, W);

            // ---- molduras
            Linha(g, 1, W, m.SupEsq, m.H, m.SupDir);
            Linha(g, ySeparador, W, m.TEsq, m.H, m.TDir);
            Linha(g, H - 1, W, m.InfEsq, m.H, m.InfDir);
            for (int r = yChat; r < ySeparador; r++) { Borda(g, r, 0, m.V); Borda(g, r, W - 1, m.V); }
            for (int r = ySeparador + 1; r < H - 1; r++) { Borda(g, r, 0, m.V); Borda(g, r, W - 1, m.V); }
            if (comMembros)
            {
                Borda(g, 1, xDivisao, m.TCima);
                Borda(g, ySeparador, xDivisao, m.TBaixo);
                for (int r = yChat; r < ySeparador; r++) Borda(g, r, xDivisao, m.V);
            }

            Titulo(g, 1, 2, xDivisao - 1, _tema.RotuloConversa);
            Titulo(g, ySeparador, 2, W - 2, _mascarar ? _tema.RotuloSenha : _tema.RotuloMensagem);
            string dicas = _tema.Dicas(W >= 90);
            Texto(g, H - 1, 2, W - 2, _tema.Titulo(Cortar(dicas, W - 8)), _tema.CorFraca);

            DesenharConversa(g, yChat, alturaChat, xTexto, larguraTexto);
            if (comMembros) DesenharMembros(g, yChat, alturaChat, xDivisao + 2, W - 2);

            // ---- caixa de mensagem
            int primeira = Math.Clamp(cursorEntrada.linha - (nEntrada - 1), 0, Math.Max(0, linhasEntrada.Count - nEntrada));
            for (int i = 0; i < nEntrada; i++)
            {
                int c = 2;
                foreach (var gl in linhasEntrada[primeira + i]) c = Colocar(g, ySeparador + 1 + i, c, W - 2, gl);
            }
            cursor = (ySeparador + 1 + (cursorEntrada.linha - primeira), 2 + cursorEntrada.coluna);

            if (_aviso is not null)
            {
                DesenharAlerta(g, W, H);
                cursor = null;
            }
        }

        Emitir(g, cursor);
    }

    private void DesenharAbertura(Celula[,] g, int W, int H)
    {
        var linhas = _abertura!;
        int visiveis = Math.Min(linhas.Length, (int)(_relogio.Elapsed / PassoAbertura) + 1);
        // Em janelas estreitas o logotipo não cabe: mostra só o texto.
        int inicioTexto = Array.FindIndex(linhas, l => l.Length == 1 && l[0].Texto.Length == 0);
        int pular = linhas.Max(l => LarguraTexto(string.Concat(l.Select(t => t.Texto)))) > W - 4 && inicioTexto > 0 ? inicioTexto + 1 : 0;

        var mostrar = linhas.Skip(pular).ToArray();
        int largura = mostrar.Max(l => LarguraTexto(string.Concat(l.Select(t => t.Texto))));
        int x = Math.Max(1, (W - largura) / 2);
        int y = Math.Max(0, (H - mostrar.Length) / 2);
        int n = Math.Max(0, visiveis - pular);

        int ultimaColuna = x, ultimaLinha = y;
        for (int i = 0; i < n && i < mostrar.Length && y + i < H; i++)
        {
            int c = x;
            foreach (var t in mostrar[i]) c = Texto(g, y + i, c, W, t.Texto, t.Cor is { } cor ? (int)cor : _tema.CorTexto, negrito: t.Negrito);
            ultimaColuna = c;
            ultimaLinha = y + i;
        }
        // cursor de bloco "piscando" no fim da última linha
        if (DateTime.Now.Millisecond < 500 && ultimaColuna + 1 < W)
            Texto(g, ultimaLinha, ultimaColuna + 1, W, "█", _tema.CorTexto);

        string pule = "PRESSIONE QUALQUER TECLA";
        Texto(g, H - 1, Math.Max(0, (W - pule.Length) / 2), W, pule, _tema.CorFraca);
    }

    private void DesenharCabecalho(Celula[,] g, int W)
    {
        int fundo = _tema.CabecalhoFundo, frente = _tema.CabecalhoFrente;
        for (int c = 0; c < W; c++) g[0, c] = new Celula(" ", frente, fundo, false);

        var agora = DateTime.Now;
        bool piscar = agora.Second % 2 == 0;
        var esquerda = _tema.CabecalhoEsquerda(_sala, _endereco);
        int larguraEsquerda = esquerda.Sum(t => LarguraTexto(t.Texto)) + 2;

        // DTG e outros extras só entram se couberem sem cortar o lado esquerdo.
        var direita = _tema.CabecalhoDireita(_conectado, piscar, agora, largo: true);
        if (larguraEsquerda + direita.Sum(t => LarguraTexto(t.Texto)) > W)
            direita = _tema.CabecalhoDireita(_conectado, piscar, agora, largo: false);

        int xDireita = Math.Max(1, W - direita.Sum(t => LarguraTexto(t.Texto)));
        int c0 = 1;
        foreach (var t in esquerda)
        {
            if (c0 >= xDireita - 1) break;
            c0 = Texto(g, 0, c0, xDireita - 1, Cortar(t.Texto, xDireita - 1 - c0), CorDe(t, frente), fundo, t.Negrito);
        }
        int cd = xDireita;
        foreach (var t in direita) cd = Texto(g, 0, cd, W, t.Texto, CorDe(t, frente), fundo, t.Negrito);
    }

    private void DesenharConversa(Celula[,] g, int y, int altura, int x, int largura)
    {
        if (largura < 5 || altura < 1) return;

        var linhas = new List<Celula[]>();
        foreach (var m in _mensagens) linhas.AddRange(LinhasDe(m, largura));

        int maxRolagem = Math.Max(0, linhas.Count - altura);
        _rolagem = Math.Clamp(_rolagem, 0, maxRolagem);
        if (_rolagem == 0) _novasAbaixo = 0;

        int fim = linhas.Count - _rolagem;
        int inicio = Math.Max(0, fim - altura);
        for (int i = inicio; i < fim; i++)
        {
            int r = y + (i - inicio);
            var linha = linhas[i];
            for (int c = 0; c < linha.Length && x + c < g.GetLength(1); c++) g[r, x + c] = linha[c];
        }

        if (linhas.Count == 0)
            Texto(g, y, x, x + largura, _tema.ConversaVazia, _tema.CorFraca);

        if (_rolagem > 0)
        {
            string t = _tema.AvisoRolagem(_novasAbaixo);
            int r = y + altura - 1;
            for (int c = x - 1; c < x + largura + 1; c++) g[r, c] = new Celula(" ", _tema.BarraFrente, _tema.BarraFundo, false);
            Texto(g, r, x, x + largura, Cortar(t, largura), _tema.BarraFrente, _tema.BarraFundo, negrito: true);
        }
    }

    private void DesenharMembros(Celula[,] g, int y, int altura, int x0, int x1)
    {
        Titulo(g, 1, x0, x1, _tema.RotuloMembros(_usuarios.Count, _listaCompleta));

        bool sobra = _usuarios.Count > altura;
        int mostrar = sobra ? altura - 1 : _usuarios.Count;
        for (int i = 0; i < mostrar; i++)
        {
            string nome = _usuarios[i];
            bool eu = nome.Equals(_voce, StringComparison.OrdinalIgnoreCase);
            int c = x0;
            foreach (var t in _tema.Membro(i + 1, nome, eu))
                c = Texto(g, y + i, c, x1, Cortar(t.Texto, Math.Max(1, x1 - c)), CorDe(t, _tema.CorTexto), negrito: t.Negrito);
        }
        if (sobra)
            Texto(g, y + altura - 1, x0, x1, $"… +{_usuarios.Count - mostrar}", _tema.CorFraca);
        if (!_listaCompleta && altura > _usuarios.Count + 1 && x1 - x0 > 14)
            Texto(g, y + altura - 1, x0, x1, Cortar("(servidor v1: lista parcial)", x1 - x0), _tema.CorFraca);
    }

    /// <summary>Aviso em caixa no centro da tela (ex.: conexão perdida).</summary>
    private void DesenharAlerta(Celula[,] g, int W, int H)
    {
        string texto = _tema.Alerta(_aviso!);
        int largura = Math.Min(W - 4, LarguraTexto(texto) + 4);
        int x = (W - largura) / 2, y = H / 2 - 1;
        int f = _tema.AlertaFrente, b = _tema.AlertaFundo;
        var m = _tema.Moldura;
        for (int r = y; r < y + 3; r++)
            for (int c = x; c < x + largura; c++)
                g[r, c] = new Celula(" ", f, b, false);
        for (int c = x; c < x + largura; c++) { g[y, c] = new(m.H.ToString(), f, b, true); g[y + 2, c] = new(m.H.ToString(), f, b, true); }
        g[y, x] = new(m.SupEsq.ToString(), f, b, true); g[y, x + largura - 1] = new(m.SupDir.ToString(), f, b, true);
        g[y + 2, x] = new(m.InfEsq.ToString(), f, b, true); g[y + 2, x + largura - 1] = new(m.InfDir.ToString(), f, b, true);
        g[y + 1, x] = new(m.V.ToString(), f, b, true); g[y + 1, x + largura - 1] = new(m.V.ToString(), f, b, true);
        if (_tema.TituloAlerta is { } titulo) Texto(g, y, x + 2, x + largura - 2, _tema.Titulo(titulo), f, b, true);
        Texto(g, y + 1, x + 2, x + largura - 2, Cortar(texto, largura - 4), f, b, true);
    }

    /// <summary>Quebra o texto digitado em linhas da caixa de mensagem e acha onde o cursor fica.</summary>
    private (List<List<Glifo>> linhas, (int linha, int coluna) cursor) MontarEntrada(int largura)
    {
        var glifos = new List<Glifo>();
        glifos.AddRange(Glifos(_tema.Prompt(_nome), _tema.CorFraca, false));
        int indiceCursor = glifos.Count;

        string antes = _entrada.ToString(0, _cursor);
        string texto = _entrada.ToString();
        int corTexto = _tema.CorTexto;
        if (_mascarar)
        {
            int nAntes = antes.EnumerateRunes().Count();
            int nTotal = texto.EnumerateRunes().Count();
            for (int i = 0; i < nTotal; i++) glifos.Add(new Glifo("*", 1, corTexto, false));
            indiceCursor += nAntes;
        }
        else
        {
            var g1 = Glifos(antes, corTexto, false).ToList();
            indiceCursor += g1.Count;
            glifos.AddRange(g1);
            glifos.AddRange(Glifos(texto[_cursor..], corTexto, false));
        }

        var linhas = new List<List<Glifo>> { new() };
        int col = 0;
        (int, int) cursor = (0, 0);
        for (int i = 0; i <= glifos.Count; i++)
        {
            int l = i < glifos.Count ? glifos[i].L : 1; // a posição final do cursor também ocupa espaço
            if (col + l > largura) { linhas.Add([]); col = 0; }
            if (i == indiceCursor) cursor = (linhas.Count - 1, col);
            if (i == glifos.Count) break;
            linhas[^1].Add(glifos[i]);
            col += l;
        }
        return (linhas, cursor);
    }

    // ------------------------------------------------- quebra das mensagens

    private List<Celula[]> LinhasDe(Mensagem m, int largura)
    {
        if (m.LarguraCache == largura) return m.Linhas;

        var glifos = new List<Glifo>();
        foreach (var t in m.Trechos) glifos.AddRange(Glifos(t.Texto, CorDe(t, Padrao), t.Negrito));

        // Linhas seguintes ficam alinhadas depois do horário.
        int recuo = m.Trechos.Length > 1 ? Math.Min(LarguraTexto(m.Trechos[0].Texto), largura / 3) : 0;

        var linhas = new List<List<Glifo>>();
        var atual = new List<Glifo>();
        int ocupado = 0;
        foreach (var g in glifos)
        {
            int limite = largura - (linhas.Count == 0 ? 0 : recuo);
            if (ocupado + g.L > limite && atual.Count > 0)
            {
                // Quebra no último espaço, se houver um razoável; senão corta a palavra.
                int espaco = atual.FindLastIndex(x => x.T == " ");
                var resto = new List<Glifo>();
                int minimo = linhas.Count == 0 ? recuo : 0;
                if (g.T != " " && espaco > minimo)
                {
                    resto = atual.GetRange(espaco + 1, atual.Count - espaco - 1);
                    atual.RemoveRange(espaco, atual.Count - espaco);
                }
                linhas.Add(atual);
                atual = resto;
                ocupado = resto.Sum(x => x.L);
                if (g.T == " " && atual.Count == 0) continue;
            }
            atual.Add(g);
            ocupado += g.L;
        }
        linhas.Add(atual);

        var resultado = new List<Celula[]>(linhas.Count);
        for (int i = 0; i < linhas.Count; i++)
        {
            var celulas = new Celula[largura];
            Array.Fill(celulas, Celula.Vazia);
            int c = i == 0 ? 0 : recuo;
            foreach (var g in linhas[i])
            {
                if (c + g.L > largura) break;
                celulas[c] = new Celula(g.T, g.Fg, Padrao, g.Negrito);
                if (g.L == 2) celulas[c + 1] = new Celula("", g.Fg, Padrao, g.Negrito);
                c += g.L;
            }
            resultado.Add(celulas);
        }

        m.LarguraCache = largura;
        m.Linhas = resultado;
        return resultado;
    }

    // ------------------------------------------------------------- desenho

    private static int CorDe(Trecho t, int padrao) => t.Cor is { } c ? (int)c : padrao;

    private void Linha(Celula[,] g, int r, int W, char esq, char meio, char dir)
    {
        Borda(g, r, 0, esq);
        for (int c = 1; c < W - 1; c++) Borda(g, r, c, meio);
        Borda(g, r, W - 1, dir);
    }

    private void Borda(Celula[,] g, int r, int c, char ch) => g[r, c] = new Celula(ch.ToString(), _tema.CorBorda, Padrao, false);

    private void Titulo(Celula[,] g, int r, int x0, int x1, string rotulo)
    {
        if (x1 - x0 < 6) return;
        string t = _tema.Titulo(rotulo);
        if (LarguraTexto(t) > x1 - x0) t = _tema.Titulo(Cortar(rotulo, Math.Max(1, x1 - x0 - (LarguraTexto(t) - LarguraTexto(rotulo)))));
        Texto(g, r, x0, x1, t, _tema.CorTitulo, negrito: true);
    }

    /// <summary>Escreve texto a partir da coluna c sem passar de cMax; retorna a coluna seguinte.</summary>
    private int Texto(Celula[,] g, int r, int c, int cMax, string s, int fg, int bg = Padrao, bool negrito = false)
    {
        foreach (var gl in Glifos(s, fg, negrito)) c = Colocar(g, r, c, cMax, gl, bg);
        return c;
    }

    private static int Colocar(Celula[,] g, int r, int c, int cMax, Glifo gl, int bg = Padrao)
    {
        if (c + gl.L > cMax || c < 0 || r < 0 || r >= g.GetLength(0)) return c;
        g[r, c] = new Celula(gl.T, gl.Fg, bg, gl.Negrito);
        if (gl.L == 2) g[r, c + 1] = new Celula("", gl.Fg, bg, gl.Negrito);
        return c + gl.L;
    }

    private void Emitir(Celula[,] g, (int linha, int coluna)? cursor)
    {
        int H = g.GetLength(0), W = g.GetLength(1);
        var sb = new StringBuilder(W * H * 2);
        sb.Append("\x1b[?25l");
        if (_redesenharTudo || _anterior is null || _anterior.GetLength(0) != H || _anterior.GetLength(1) != W)
        {
            sb.Append(Sgr(Padrao, Padrao, false)).Append("\x1b[2J");
            _anterior = null;
        }

        for (int r = 0; r < H; r++)
        {
            if (_anterior is not null && LinhaIgual(g, _anterior, r, W)) continue;

            sb.Append("\x1b[").Append(r + 1).Append(";1H");
            int fg = int.MinValue, bg = int.MinValue; bool neg = false;
            for (int c = 0; c < W; c++)
            {
                var cel = g[r, c];
                if (cel.T == "") continue; // metade direita de um caractere largo
                if (cel.Fg != fg || cel.Bg != bg || cel.Negrito != neg)
                {
                    fg = cel.Fg; bg = cel.Bg; neg = cel.Negrito;
                    sb.Append(Sgr(fg, bg, neg));
                }
                sb.Append(cel.T ?? " ");
            }
            sb.Append("\x1b[0m");
        }

        if (cursor is { } cur)
            sb.Append(Sgr(Padrao, Padrao, false)).Append("\x1b[").Append(cur.linha + 1).Append(';').Append(cur.coluna + 1).Append("H\x1b[?25h");

        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
        _anterior = g;
        _redesenharTudo = false;
    }

    private static bool LinhaIgual(Celula[,] a, Celula[,] b, int r, int W)
    {
        for (int c = 0; c < W; c++) if (a[r, c] != b[r, c]) return false;
        return true;
    }

    private string Sgr(int fg, int bg, bool negrito)
    {
        // Sem cores (--sem-cor), um fundo colorido vira vídeo reverso para barras e avisos continuarem visíveis.
        if (!_cores) return $"\x1b[0{(negrito ? ";1" : "")}{(bg != Padrao ? ";7" : "")}m";
        // "Padrão" dentro da tela quer dizer: a cor do tema (ex.: verde sobre preto no tema militar).
        if (fg == Padrao) fg = _tema.CorTexto;
        if (bg == Padrao) bg = _tema.Fundo;
        return $"\x1b[0{(negrito ? ";1" : "")};{CodigoCor(fg, false)};{CodigoCor(bg, true)}m";
    }

    private static string CodigoCor(int cor, bool fundo)
    {
        if (cor == Padrao) return fundo ? "49" : "39";
        int b = (ConsoleColor)cor switch
        {
            ConsoleColor.Black => 30, ConsoleColor.DarkRed => 31, ConsoleColor.DarkGreen => 32,
            ConsoleColor.DarkYellow => 33, ConsoleColor.DarkBlue => 34, ConsoleColor.DarkMagenta => 35,
            ConsoleColor.DarkCyan => 36, ConsoleColor.Gray => 37, ConsoleColor.DarkGray => 90,
            ConsoleColor.Red => 91, ConsoleColor.Green => 92, ConsoleColor.Yellow => 93,
            ConsoleColor.Blue => 94, ConsoleColor.Magenta => 95, ConsoleColor.Cyan => 96,
            _ => 97,
        };
        return (b + (fundo ? 10 : 0)).ToString();
    }

    // ------------------------------------------------ largura de caracteres

    /// <summary>Divide em caracteres visíveis com a largura de cada um (acentos combinados, emoji e CJK).</summary>
    private static IEnumerable<Glifo> Glifos(string s, int fg, bool negrito)
    {
        Glifo? pendente = null;
        foreach (Rune r in s.EnumerateRunes())
        {
            Rune rr = r;
            if (rr.Value == '\t') rr = new Rune(' ');
            if (Rune.IsControl(rr)) continue;
            int l = LarguraRune(rr);
            if (l == 0)
            {
                if (pendente is { } p) pendente = p with { T = p.T + rr.ToString() };
                continue;
            }
            if (pendente is { } anterior) yield return anterior;
            pendente = new Glifo(rr.ToString(), l, fg, negrito);
        }
        if (pendente is { } ultimo) yield return ultimo;
    }

    private static int LarguraTexto(string s) => Glifos(s, Padrao, false).Sum(g => g.L);

    private static string Cortar(string s, int largura)
    {
        if (largura <= 0) return "";
        if (LarguraTexto(s) <= largura) return s;
        var sb = new StringBuilder();
        int usado = 0;
        foreach (var g in Glifos(s, Padrao, false))
        {
            if (usado + g.L > largura - 1) break;
            sb.Append(g.T);
            usado += g.L;
        }
        return sb.Append('…').ToString();
    }

    private static int LarguraRune(Rune r)
    {
        int v = r.Value;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        if (v == 0x200B) return 0;
        bool largo =
            (v >= 0x1100 && v <= 0x115F) || v == 0x2329 || v == 0x232A ||
            (v >= 0x2E80 && v <= 0xA4CF && v != 0x303F) || (v >= 0xAC00 && v <= 0xD7A3) ||
            (v >= 0xF900 && v <= 0xFAFF) || (v >= 0xFE30 && v <= 0xFE4F) ||
            (v >= 0xFF00 && v <= 0xFF60) || (v >= 0xFFE0 && v <= 0xFFE6) ||
            (v >= 0x1F300 && v <= 0x1F64F) || (v >= 0x1F680 && v <= 0x1F6FF) ||
            (v >= 0x1F900 && v <= 0x1F9FF) || (v >= 0x1FA70 && v <= 0x1FAFF) ||
            (v >= 0x20000 && v <= 0x3FFFD);
        return largo ? 2 : 1;
    }

    // ------------------------------------------------------------- Windows

    /// <summary>No Windows, liga o processamento de sequências ANSI no console (Windows 10+).</summary>
    private static class Vt
    {
        private const int SaidaPadrao = -11;
        private const uint ProcessarVt = 0x0004;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int n);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr h, out uint modo);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr h, uint modo);

        public static bool Habilitar()
        {
            if (!OperatingSystem.IsWindows()) return true;
            try
            {
                IntPtr h = GetStdHandle(SaidaPadrao);
                return GetConsoleMode(h, out uint modo) && ((modo & ProcessarVt) != 0 || SetConsoleMode(h, modo | ProcessarVt));
            }
            catch (Exception) { return false; }
        }
    }
}
