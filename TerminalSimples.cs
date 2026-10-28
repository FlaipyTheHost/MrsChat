using System.Text;

namespace MrsChat;

/// <summary>
/// Interface linha a linha (--simples): as mensagens que chegam são impressas ACIMA da linha
/// de digitação, sem apagar o que você está escrevendo.
///
/// Recursos: histórico com ↑/↓, Esc limpa a linha, senha mascarada com *, e um modo
/// ReadLine/WriteLine puro quando a entrada ou saída é redirecionada (scripts, pipes).
/// </summary>
sealed class TerminalSimples : IInterfaceChat
{
    private readonly object _trava = new();
    private readonly StringBuilder _entrada = new();
    private readonly List<string> _historico = [];
    private int _posHistorico;
    private bool _interativo;
    private readonly bool _cores;
    private bool _promptVisivel;
    private int _linhaInicio;

    public string Prompt { get; set; } = "> ";
    public bool Mascarar { get; set; }
    public bool Interativo => _interativo;

    public TerminalSimples(bool cores)
    {
        _interativo = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        _cores = cores && !Console.IsOutputRedirected;
        if (_interativo) Console.OutputEncoding = Encoding.UTF8;
    }

    // Os painéis só existem na tela cheia; aqui essas informações não são exibidas.
    public void DefinirSala(string? sala, string endereco) { }
    public void DefinirUsuarios(IReadOnlyCollection<string> nomes, string voce, bool listaCompleta) { }
    public void DefinirConectado(bool conectado) { }
    public void AguardarTecla(string aviso, CancellationToken ct) => Escrever(new Trecho(aviso, ConsoleColor.Red));
    public void Dispose() { }

    // --------------------------------------------------------------- saída

    /// <summary>Escreve uma linha completa (em partes coloridas) acima da linha de digitação.</summary>
    public void Escrever(params Trecho[] trechos)
    {
        lock (_trava)
        {
            Seguro(() =>
            {
                ApagarEntrada();
                foreach (var t in trechos)
                {
                    if (_cores && t.Cor is { } c) Console.ForegroundColor = c;
                    Console.Write(t.Texto);
                    if (_cores) Console.ResetColor();
                }
                Console.WriteLine();
                DesenharEntrada();
            }, () => Console.WriteLine(string.Concat(trechos.Select(t => t.Texto))));
        }
    }

    public void Limpar()
    {
        lock (_trava)
        {
            Seguro(() =>
            {
                Console.Clear();
                _promptVisivel = false;
                DesenharEntrada();
            }, () => { });
        }
    }

    // -------------------------------------------------------------- entrada

    /// <summary>Lê uma linha digitada. Retorna null se cancelado (conexão caiu) ou Ctrl+D.</summary>
    public string? LerLinha(CancellationToken ct)
    {
        if (!_interativo)
        {
            if (ct.IsCancellationRequested) return null;
            return Console.ReadLine();
        }

        lock (_trava) Seguro(DesenharEntrada, () => { });

        while (true)
        {
            while (!TemTecla())
            {
                if (ct.IsCancellationRequested) return null;
                Thread.Sleep(25);
            }
            if (ct.IsCancellationRequested) return null;
            if (!_interativo) return Console.ReadLine();

            ConsoleKeyInfo k = Console.ReadKey(intercept: true);
            lock (_trava)
            {
                switch (k.Key)
                {
                    case ConsoleKey.Enter:
                    {
                        string linha = _entrada.ToString();
                        bool eraSenha = Mascarar;
                        ApagarEntrada();
                        _entrada.Clear();
                        if (!eraSenha && linha.Trim().Length > 0 && (_historico.Count == 0 || _historico[^1] != linha))
                            _historico.Add(linha);
                        _posHistorico = _historico.Count;
                        DesenharEntrada();
                        return linha;
                    }
                    case ConsoleKey.Backspace:
                        if (_entrada.Length > 0)
                        {
                            _entrada.Length--;
                            Redesenhar();
                        }
                        break;
                    case ConsoleKey.Escape:
                        _entrada.Clear();
                        Redesenhar();
                        break;
                    case ConsoleKey.UpArrow when !Mascarar:
                        if (_posHistorico > 0) TrocarEntrada(_historico[--_posHistorico]);
                        break;
                    case ConsoleKey.DownArrow when !Mascarar:
                        if (_posHistorico < _historico.Count - 1) TrocarEntrada(_historico[++_posHistorico]);
                        else { _posHistorico = _historico.Count; TrocarEntrada(""); }
                        break;
                    case ConsoleKey.D when k.Modifiers.HasFlag(ConsoleModifiers.Control) && _entrada.Length == 0:
                        return null;
                    default:
                        if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar) && _entrada.Length < Protocolo.TamanhoMaximoLinha)
                        {
                            _entrada.Append(k.KeyChar);
                            Console.Write(Mascarar ? '*' : k.KeyChar);
                            AjustarRolagem();
                        }
                        break;
                }
            }
        }
    }

    // --------------------------------------------------------------- apoio

    private void TrocarEntrada(string texto)
    {
        _entrada.Clear().Append(texto);
        Redesenhar();
    }

    private void Redesenhar()
    {
        ApagarEntrada();
        DesenharEntrada();
    }

    private void DesenharEntrada()
    {
        if (!_interativo || _promptVisivel) return;
        _linhaInicio = Console.CursorTop;
        if (_cores) Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(Prompt);
        if (_cores) Console.ResetColor();
        Console.Write(Mascarar ? new string('*', _entrada.Length) : _entrada.ToString());
        _promptVisivel = true;
        AjustarRolagem();
    }

    private void ApagarEntrada()
    {
        if (!_interativo || !_promptVisivel) return;
        int largura = Largura();
        int linhas = LinhasOcupadas(largura);
        for (int i = 0; i < linhas; i++)
        {
            int linha = _linhaInicio + i;
            if (linha >= Console.BufferHeight) break;
            Console.SetCursorPosition(0, linha);
            Console.Write(new string(' ', largura - 1));
        }
        Console.SetCursorPosition(0, _linhaInicio);
        _promptVisivel = false;
    }

    /// <summary>Se a linha de digitação quebrou no fim da tela, o terminal rolou e o início subiu.</summary>
    private void AjustarRolagem()
    {
        int ultima = _linhaInicio + LinhasOcupadas(Largura()) - 1;
        int limite = Console.BufferHeight - 1;
        if (ultima > limite) _linhaInicio = Math.Max(0, _linhaInicio - (ultima - limite));
    }

    private int LinhasOcupadas(int largura)
    {
        int total = Prompt.Length + _entrada.Length;
        return Math.Max(1, (total + largura - 1) / largura);
    }

    private static int Largura()
    {
        try { return Math.Max(10, Console.WindowWidth); } catch { return 80; }
    }

    private bool TemTecla()
    {
        try { return Console.KeyAvailable; }
        catch (InvalidOperationException) { _interativo = false; return true; }
    }

    /// <summary>Se o terminal não suportar posicionamento de cursor, cai para o modo simples.</summary>
    private void Seguro(Action acao, Action alternativa)
    {
        if (!_interativo) { alternativa(); return; }
        try { acao(); }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or InvalidOperationException or PlatformNotSupportedException)
        {
            _interativo = false;
            _promptVisivel = false;
            alternativa();
        }
    }
}
