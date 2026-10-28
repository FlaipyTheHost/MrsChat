using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MrsChat;

sealed partial class ClienteChat
{
    private static readonly UTF8Encoding Utf8SemBom = new(false);

    private readonly OpcoesCliente _op;
    private readonly IInterfaceChat _t;
    private readonly SemaphoreSlim _travaEnvio = new(1, 1);
    private string _nome;
    private NetworkStream? _stream;
    private volatile bool _aguardandoSenha;
    private volatile bool _saindo;

    // Fim do "aperto de mãos" (nome e senha aceitos). Com --senha, mensagens digitadas antes disso
    // esperam; senão a primeira poderia chegar ao servidor antes da senha e ser lida no lugar dela.
    private readonly TaskCompletionSource _pronto = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan EsperaPronto = TimeSpan.FromSeconds(2); // servidor v1 não confirma nada

    // Painel de integrantes
    private readonly HashSet<string> _usuarios = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _travaUsuarios = new();
    private bool _servidorV2;
    private int _listasPendentes; // /usuarios pedidos automaticamente (a resposta não aparece no chat)

    /// <summary>Mensagem para mostrar no terminal normal depois que a tela cheia fechar.</summary>
    public string? MensagemFinal { get; private set; }

    public ClienteChat(OpcoesCliente op, IInterfaceChat ui)
    {
        _op = op;
        _t = ui;
        _nome = op.Nome!;
    }

    public async Task<int> ExecutarAsync(CancellationToken ctExterno)
    {
        _t.DefinirSala(null, _op.Endereco);
        _t.DefinirNome(_nome);
        Mostrar(TipoEvento.Local, $"Conectando a {_op.Endereco}...");

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
            MensagemFinal = $"Não foi possível conectar a {_op.Endereco}: {DescreverErro(ex)}\n" +
                            "Confira o endereço e a porta, e se o servidor está rodando.";
            return 1;
        }

        _stream = tcp.GetStream();
        _t.DefinirConectado(true);
        lock (_travaUsuarios) _usuarios.Add(_nome);
        AtualizarPainel();
        Mostrar(TipoEvento.Sucesso, "Conectado! Digite /ajuda para ver os comandos.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctExterno);
        await EnviarAsync(_nome, cts.Token); // 1ª linha = nome (igual à v1)

        var recebimento = Task.Run(() => ReceberAsync(cts));
        var digitacao = Task.Factory.StartNew(() => LacoDeDigitacao(cts), TaskCreationOptions.LongRunning).Unwrap();

        await Task.WhenAny(recebimento, digitacao);
        cts.Cancel();

        if (!_saindo && !ctExterno.IsCancellationRequested)
        {
            _t.DefinirConectado(false);
            _t.AguardarTecla("Conexão com o servidor encerrada. Pressione uma tecla para sair.", ctExterno);
            MensagemFinal = "Conexão com o servidor encerrada.";
            return 1;
        }

        try { await EnviarAsync("exit", CancellationToken.None); } catch { }
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

            if (_op.Senha is not null && !_pronto.Task.IsCompleted)
            {
                try { await _pronto.Task.WaitAsync(EsperaPronto, cts.Token); }
                catch (TimeoutException) { _pronto.TrySetResult(); }
            }
            if (_aguardandoSenha)
            {
                // O pedido de senha chegou enquanto esperávamos: esta linha é a senha.
                _aguardandoSenha = false;
                _t.Mascarar = false;
                await EnviarAsync(linha, cts.Token);
                continue;
            }

            if (linha.StartsWith('/') || linha.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                if (!await ComandoLocalAsync(linha, cts.Token)) { _saindo = true; return; }
                continue;
            }

            await EnviarAsync(linha, cts.Token);
            _t.Escrever(new Evento(TipoEvento.Propria, linha, _nome));
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
                foreach (var l in AjudaComandos) Mostrar(TipoEvento.Local, l);
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
        "  /limpar                limpa a conversa",
        "  /sair                  sai do chat (ou Ctrl+C)",
        "Teclas: ↑/↓ histórico · PgUp/PgDn rola a conversa · Tab completa nomes · Esc limpa a linha",
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
            Mostrar(TipoEvento.Aviso, "Esta sala exige senha. Digite a senha e tecle Enter.");
            _aguardandoSenha = true;
            _t.Mascarar = true;
            _pronto.TrySetResult(); // libera a digitação: a próxima linha é a senha
            return;
        }

        if (linha.StartsWith(Protocolo.PrefixoSeuNome, StringComparison.Ordinal))
        {
            string novo = linha[Protocolo.PrefixoSeuNome.Length..].Trim();
            if (novo.Length == 0) return;
            if (novo != _nome)
                Mostrar(TipoEvento.Aviso, $"Seu nome na sala agora é {novo}.");

            _pronto.TrySetResult();
            lock (_travaUsuarios) { _usuarios.Remove(_nome); _usuarios.Add(novo); }
            _nome = novo;
            _t.DefinirNome(_nome);

            if (!_servidorV2)
            {
                // Só o servidor v2 manda esta linha. Agora dá para pedir a lista sem que ela vire
                // uma mensagem para todos (no servidor v1, "/usuarios" seria divulgado como texto).
                _servidorV2 = true;
                Interlocked.Increment(ref _listasPendentes);
                await EnviarAsync("/usuarios", ct);
            }
            AtualizarPainel();
            return;
        }

        if (linha.StartsWith(Protocolo.PrefixoSala, StringComparison.Ordinal))
        {
            _t.DefinirSala(linha[Protocolo.PrefixoSala.Length..].Trim(), _op.Endereco);
            return;
        }

        if (linha.StartsWith(Protocolo.PrefixoOnline, StringComparison.Ordinal) || linha == Protocolo.PrefixoServidor + "Ninguém online.")
        {
            AplicarListaOnline(linha);
            if (Interlocked.Decrement(ref _listasPendentes) >= 0) return; // pedido automático: não exibe
            Interlocked.Exchange(ref _listasPendentes, 0);
        }

        AcompanharEntradasESaidas(linha);
        _t.Escrever(Classificar(linha));
    }

    // ------------------------------------------------------------ integrantes

    [GeneratedRegex(@"^(\S+) conectou-se\.$")] private static partial Regex ReEntrou();
    [GeneratedRegex(@"^(\S+) desconectou\.$")] private static partial Regex ReSaiu();
    [GeneratedRegex(@"^(\S+) agora é (\S+)\.$")] private static partial Regex ReRenomeou();

    private void AplicarListaOnline(string linha)
    {
        int dp = linha.IndexOf("): ", StringComparison.Ordinal);
        var nomes = dp < 0 ? [] : linha[(dp + 3)..].Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lock (_travaUsuarios)
        {
            _usuarios.Clear();
            foreach (var n in nomes) _usuarios.Add(n);
            _usuarios.Add(_nome);
        }
        AtualizarPainel();
    }

    private void AcompanharEntradasESaidas(string linha)
    {
        bool mudou = false;
        lock (_travaUsuarios)
        {
            Match m;
            if ((m = ReEntrou().Match(linha)).Success) mudou = _usuarios.Add(m.Groups[1].Value);
            else if ((m = ReSaiu().Match(linha)).Success) mudou = _usuarios.Remove(m.Groups[1].Value);
            else if ((m = ReRenomeou().Match(linha)).Success)
            {
                _usuarios.Remove(m.Groups[1].Value);
                _usuarios.Add(m.Groups[2].Value);
                mudou = true;
            }
        }
        if (mudou) AtualizarPainel();
    }

    private void AtualizarPainel()
    {
        string[] copia;
        lock (_travaUsuarios) copia = [.. _usuarios];
        // Com servidor v1 não há como pedir a lista: só aparece quem entrou depois de você.
        _t.DefinirUsuarios(copia, _nome, listaCompleta: _servidorV2);
    }

    // --------------------------------------------------------------- exibição

    private void Mostrar(TipoEvento tipo, string texto) => _t.Escrever(new Evento(tipo, texto));

    /// <summary>Transforma uma linha do protocolo (texto puro, igual à v1) num evento.</summary>
    private static Evento Classificar(string linha)
    {
        if (linha.StartsWith(Protocolo.PrefixoServidor, StringComparison.Ordinal))
            return new(TipoEvento.Servidor, linha[Protocolo.PrefixoServidor.Length..]);

        Match m;
        if ((m = RePrivadoDe().Match(linha)).Success)
            return new(TipoEvento.Privado, m.Groups[2].Value, m.Groups[1].Value, "de");
        if ((m = RePrivadoPara().Match(linha)).Success)
            return new(TipoEvento.Privado, m.Groups[2].Value, m.Groups[1].Value, "para");
        if ((m = ReAcao().Match(linha)).Success)
            return new(TipoEvento.Acao, m.Groups[2].Value, m.Groups[1].Value);
        if ((m = ReEntrou().Match(linha)).Success) return new(TipoEvento.Entrou, Autor: m.Groups[1].Value);
        if ((m = ReSaiu().Match(linha)).Success) return new(TipoEvento.Saiu, Autor: m.Groups[1].Value);
        if ((m = ReRenomeou().Match(linha)).Success) return new(TipoEvento.Renomeou, Autor: m.Groups[1].Value, Extra: m.Groups[2].Value);

        int dp = linha.IndexOf(": ", StringComparison.Ordinal);
        if (dp > 0 && dp <= 32 && !linha[..dp].Contains(' '))
            return new(TipoEvento.Mensagem, linha[(dp + 2)..], linha[..dp]);

        return new(TipoEvento.Texto, linha);
    }

    [GeneratedRegex(@"^\(privado\) (\S+): (.*)$")] private static partial Regex RePrivadoDe();
    [GeneratedRegex(@"^\(privado para (\S+)\) (.*)$")] private static partial Regex RePrivadoPara();
    [GeneratedRegex(@"^\* (\S+) (.+)$")] private static partial Regex ReAcao();

    private static string DescreverErro(Exception ex) => ex switch
    {
        OperationCanceledException => "tempo esgotado",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "conexão recusada",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "endereço não encontrado",
        SocketException s => s.Message,
        _ => ex.Message,
    };
}
