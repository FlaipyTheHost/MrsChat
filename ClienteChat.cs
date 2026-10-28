using System.Net.Sockets;
using System.Text;

namespace MrsChat;

sealed class ClienteChat
{
    private static readonly UTF8Encoding Utf8SemBom = new(false);
    private static readonly ConsoleColor[] Paleta =
    [
        ConsoleColor.Cyan, ConsoleColor.Green, ConsoleColor.Yellow, ConsoleColor.Magenta,
        ConsoleColor.Blue, ConsoleColor.Red, ConsoleColor.DarkCyan, ConsoleColor.DarkGreen,
        ConsoleColor.DarkYellow, ConsoleColor.DarkMagenta,
    ];

    private readonly OpcoesCliente _op;
    private readonly Terminal _t;
    private readonly SemaphoreSlim _travaEnvio = new(1, 1);
    private string _nome;
    private NetworkStream? _stream;
    private volatile bool _aguardandoSenha;
    private volatile bool _saindo;

    public ClienteChat(OpcoesCliente op, Terminal terminal)
    {
        _op = op;
        _t = terminal;
        _nome = op.Nome!;
    }

    public async Task<int> ExecutarAsync(CancellationToken ctExterno)
    {
        _t.Escrever($"MrsChat v{Protocolo.Versao}", ConsoleColor.Cyan);
        _t.Escrever("--------------------------------------------", ConsoleColor.DarkGray);
        _t.Escrever($"Conectando a {_op.Endereco}...", ConsoleColor.DarkGray);

        using var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ctExterno);
            prazo.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(_op.Host!, _op.Porta, prazo.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            if (ctExterno.IsCancellationRequested) return 130;
            _t.Escrever($"Não foi possível conectar a {_op.Endereco}: {DescreverErro(ex)}", ConsoleColor.Red);
            _t.Escrever("Confira o endereço e a porta, e se o servidor está rodando.", ConsoleColor.DarkGray);
            return 1;
        }

        _stream = tcp.GetStream();
        _t.Escrever("Conectado! Digite /ajuda para ver os comandos.", ConsoleColor.Green);
        _t.Prompt = $"{_nome}> ";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctExterno);
        await EnviarAsync(_nome, cts.Token); // 1ª linha = nome (igual à v1)

        var recebimento = Task.Run(() => ReceberAsync(cts));
        var digitacao = Task.Factory.StartNew(() => LacoDeDigitacao(cts), TaskCreationOptions.LongRunning).Unwrap();

        await Task.WhenAny(recebimento, digitacao);
        cts.Cancel();

        if (!_saindo && !ctExterno.IsCancellationRequested)
        {
            _t.Escrever("Conexão com o servidor encerrada.", ConsoleColor.Red);
            return 1;
        }

        try { await EnviarAsync("exit", CancellationToken.None); } catch { }
        _t.Escrever("Até logo!", ConsoleColor.DarkGray);
        return 0;
    }

    // ---------------------------------------------------------------- envio

    private async Task LacoDeDigitacao(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            string? linha = _t.LerLinha(cts.Token);
            if (linha is null)
            {
                // null = Ctrl+D (sair) ou cancelamento porque a conexão caiu.
                if (!cts.IsCancellationRequested) _saindo = true;
                return;
            }

            if (_aguardandoSenha)
            {
                _aguardandoSenha = false;
                _t.Mascarar = false;
                await EnviarAsync(linha.Trim(), cts.Token);
                continue;
            }

            linha = linha.Trim();
            if (linha.Length == 0) continue;

            if (linha.StartsWith('/') || linha.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                if (!await ComandoLocalAsync(linha, cts.Token)) { _saindo = true; return; }
                continue;
            }

            await EnviarAsync(linha, cts.Token);
            MostrarMensagem(_nome, linha);
        }
    }

    /// <returns>false para sair.</returns>
    private async Task<bool> ComandoLocalAsync(string linha, CancellationToken ct)
    {
        string cmd = linha.Split(' ', 2)[0].ToLowerInvariant();
        switch (cmd)
        {
            case "exit" or "/sair" or "/quit" or "/exit":
                return false;
            case "/limpar" or "/clear" or "/cls":
                _t.Limpar();
                return true;
            case "/ajuda" or "/help" or "/?":
                foreach (var l in AjudaComandos) _t.Escrever(l, ConsoleColor.DarkYellow);
                return true;
            default:
                // /usuarios, /msg, /nick, /me... são tratados pelo servidor (v2).
                await EnviarAsync(linha, ct);
                return true;
        }
    }

    private static readonly string[] AjudaComandos =
    [
        "Comandos:",
        "  /usuarios              quem está online",
        "  /msg <nome> <texto>    mensagem privada",
        "  /nick <novo nome>      troca seu nome",
        "  /me <ação>             ex.: /me acenou",
        "  /limpar                limpa a tela",
        "  /sair                  sai do chat (ou Ctrl+C)",
        "Teclas: ↑/↓ histórico, Esc limpa a linha.",
    ];

    private async Task EnviarAsync(string linha, CancellationToken ct)
    {
        if (_stream is null) return;
        byte[] dados = Utf8SemBom.GetBytes(linha + "\n");
        await _travaEnvio.WaitAsync(ct);
        try { await _stream.WriteAsync(dados, ct); }
        finally { _travaEnvio.Release(); }
    }

    // ----------------------------------------------------------- recebimento

    private async Task ReceberAsync(CancellationTokenSource cts)
    {
        var leitor = new LeitorLinhas(_stream!, Protocolo.TamanhoMaximoLinha * 2);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                string? linha = await leitor.LerLinhaAsync(cts.Token);
                if (linha is null) return;
                linha = linha.Trim();
                if (linha.Length > 0) await TratarLinhaAsync(linha, cts.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException) { }
    }

    private async Task TratarLinhaAsync(string linha, CancellationToken ct)
    {
        if (linha == Protocolo.PedidoSenha)
        {
            if (_op.Senha is not null)
            {
                await EnviarAsync(_op.Senha, ct);
                return;
            }
            _t.Escrever("Esta sala exige senha. Digite a senha e tecle Enter:", ConsoleColor.DarkYellow);
            _aguardandoSenha = true;
            _t.Mascarar = true;
            return;
        }

        if (linha.StartsWith(Protocolo.PrefixoSeuNome, StringComparison.Ordinal))
        {
            string novo = linha[Protocolo.PrefixoSeuNome.Length..].Trim();
            if (novo.Length > 0 && novo != _nome)
                _t.Escrever(new Terminal.Trecho(Hora() + " ", ConsoleColor.DarkGray),
                            new Terminal.Trecho($"Seu nome na sala agora é {novo}.", ConsoleColor.DarkYellow));
            if (novo.Length > 0) { _nome = novo; _t.Prompt = $"{_nome}> "; }
            return;
        }

        MostrarLinhaRecebida(linha);
    }

    // --------------------------------------------------------------- exibição

    private void MostrarLinhaRecebida(string linha)
    {
        var hora = new Terminal.Trecho(Hora() + " ", ConsoleColor.DarkGray);

        if (linha.StartsWith('[')) // [servidor] ...
        {
            _t.Escrever(hora, new Terminal.Trecho(linha, ConsoleColor.DarkYellow));
            return;
        }
        if (linha.StartsWith("(privado", StringComparison.Ordinal))
        {
            _t.Escrever(hora, new Terminal.Trecho(linha, ConsoleColor.Magenta));
            return;
        }
        if (linha.StartsWith("* ", StringComparison.Ordinal))
        {
            _t.Escrever(hora, new Terminal.Trecho(linha, ConsoleColor.DarkCyan));
            return;
        }
        if (linha.EndsWith(" conectou-se.") || linha.EndsWith(" desconectou.") || linha.Contains(" agora é "))
        {
            _t.Escrever(hora, new Terminal.Trecho(linha, ConsoleColor.DarkGray));
            return;
        }

        int dp = linha.IndexOf(": ", StringComparison.Ordinal);
        if (dp > 0 && dp <= 32 && !linha[..dp].Contains(' '))
        {
            MostrarMensagem(linha[..dp], linha[(dp + 2)..]);
            return;
        }

        _t.Escrever(hora, new Terminal.Trecho(linha));
    }

    private void MostrarMensagem(string autor, string texto) =>
        _t.Escrever(
            new Terminal.Trecho(Hora() + " ", ConsoleColor.DarkGray),
            new Terminal.Trecho(autor, CorDoNome(autor)),
            new Terminal.Trecho(": " + texto));

    /// <summary>
    /// Como na v1, nomes com uma cor (MrBlue, MrRed...) aparecem nessa cor.
    /// Os outros ganham uma cor fixa calculada a partir do nome.
    /// </summary>
    private static ConsoleColor CorDoNome(string nome)
    {
        string n = nome.ToLowerInvariant();
        if (n.Contains("blue")) return ConsoleColor.Blue;
        if (n.Contains("red")) return ConsoleColor.Red;
        if (n.Contains("purple")) return ConsoleColor.Magenta;
        if (n.Contains("green")) return ConsoleColor.Green;
        if (n.Contains("yellow")) return ConsoleColor.Yellow;
        if (n.Contains("cyan")) return ConsoleColor.Cyan;
        if (n.Contains("white")) return ConsoleColor.White;
        if (n.Contains("gray") || n.Contains("black")) return ConsoleColor.Gray;

        uint h = 2166136261; // FNV-1a: estável entre execuções (string.GetHashCode não é)
        foreach (char c in n) h = (h ^ c) * 16777619;
        return Paleta[h % (uint)Paleta.Length];
    }

    private static string Hora() => DateTime.Now.ToString("HH:mm");

    private static string DescreverErro(Exception ex) => ex switch
    {
        OperationCanceledException => "tempo esgotado",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "conexão recusada",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "endereço não encontrado",
        SocketException s => s.Message,
        _ => ex.Message,
    };
}
