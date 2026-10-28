using System.Net.Sockets;
using System.Security.Cryptography;
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
    private LeitorLinhas? _leitor;
    private CanalSeguro? _canal;
    private InfoSeguranca? _seguranca;
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
        _leitor = new LeitorLinhas(_stream, Protocolo.TamanhoMaximoLinha * 3); // base64 cifrado ocupa mais

        if (_op.SemCripto)
        {
            _t.DefinirSeguranca(null);
            Mostrar(TipoEvento.Aviso, "Criptografia desligada (--sem-cripto): as mensagens trafegam em texto puro.");
        }
        else if (await NegociarCriptografiaAsync(ctExterno) is { } erro)
        {
            MensagemFinal = erro;
            return 1;
        }

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
            case "/chave" or "/key" or "/verificar":
                MostrarChave();
                return true;
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
        "  /chave                 mostra a impressão digital completa do servidor",
        "  /sair                  sai do chat (ou Ctrl+C)",
        "Teclas: ↑/↓ histórico · PgUp/PgDn rola a conversa · Tab completa nomes · Esc limpa a linha",
    ];

    private async Task EnviarAsync(string linha, CancellationToken ct)
    {
        if (_stream is null) return;
        await _travaEnvio.WaitAsync(ct);
        try
        {
            // Cifra dentro da trava: a ordem de cifragem é a ordem em que os bytes saem (nonce = contador).
            string saida = _canal is null ? linha : _canal.Cifrar(linha);
            await _stream.WriteAsync(Utf8SemBom.GetBytes(saida + "\n"), ct);
        }
        finally { _travaEnvio.Release(); }
    }

    // ---------------------------------------------------------- criptografia

    /// <returns>null se deu certo; senão, a explicação do problema.</returns>
    private async Task<string?> NegociarCriptografiaAsync(CancellationToken ct)
    {
        Mostrar(TipoEvento.Local, "Negociando canal cifrado...");
        var (efemera, abertura) = CanalSeguro.IniciarComoCliente();
        using (efemera)
        {
            await _stream!.WriteAsync(Utf8SemBom.GetBytes(abertura + "\n"), ct);

            string? resposta;
            try
            {
                using var prazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
                prazo.CancelAfter(TimeSpan.FromSeconds(10));
                resposta = await _leitor!.LerLinhaAsync(prazo.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { resposta = null; }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) { resposta = null; }

            const string Antigo = "Atualize o MrsChatServer para a v2.3 ou conecte com --sem-cripto (sem proteção).";
            if (resposta is null)
                return "O servidor não respondeu ao pedido de criptografia. Ele deve ser de uma versão antiga.\n" + Antigo;
            if (resposta.StartsWith(Protocolo.PrefixoServidor, StringComparison.Ordinal) && !resposta.Contains("muito longo"))
                return "O servidor recusou a conexão: " + resposta[Protocolo.PrefixoServidor.Length..];
            if (!resposta.StartsWith(CanalSeguro.Prefixo + " ", StringComparison.Ordinal))
                return $"O servidor não suporta criptografia (respondeu: \"{resposta}\").\n" + Antigo;

            byte[] chaveServidor;
            try
            {
                _canal = CanalSeguro.ConcluirComoCliente(efemera, resposta, out chaveServidor);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return $"Falha na verificação criptográfica do servidor ({ex.Message}).\n" +
                       "A conexão pode estar sendo interceptada. Nada foi enviado.";
            }

            string curta = CanalSeguro.Impressao(chaveServidor), completa = CanalSeguro.Impressao(chaveServidor, 16);
            var situacao = ChavesConhecidas.Verificar(_op.Endereco, completa, _op.ConfiarNovaChave, out string? anterior);
            if (situacao == ChavesConhecidas.Situacao.Mudou && !_op.ConfiarNovaChave)
            {
                _canal.Dispose();
                _canal = null;
                return $"ATENÇÃO: a chave do servidor {_op.Endereco} MUDOU.\n" +
                       $"  antes: {anterior}\n  agora: {completa}\n" +
                       "Alguém pode estar se passando pelo servidor. Confirme a chave nova com quem administra a sala;\n" +
                       "se estiver certa, conecte de novo com --confiar-nova-chave. Nada foi enviado.";
            }

            _seguranca = new InfoSeguranca(curta, completa, situacao);
            _t.DefinirSeguranca(_seguranca);
            Mostrar(TipoEvento.Sucesso, $"Canal cifrado estabelecido ({CanalSeguro.Algoritmos}).");
            Mostrar(situacao switch
            {
                ChavesConhecidas.Situacao.Conhecida => TipoEvento.Local,
                _ => TipoEvento.Aviso,
            }, situacao switch
            {
                ChavesConhecidas.Situacao.Conhecida => $"Chave do servidor reconhecida: {curta}",
                ChavesConhecidas.Situacao.Nova => $"Primeira conexão com este servidor. Chave: {curta}. Confira com o administrador e com os outros.",
                _ => $"Chave do servidor mudou e foi aceita (--confiar-nova-chave): {curta}",
            });
            return null;
        }
    }

    private void MostrarChave()
    {
        if (_seguranca is null) { Mostrar(TipoEvento.Aviso, "Esta conexão não está cifrada."); return; }
        string[] g = _seguranca.ImpressaoCompleta.Split(' ');
        Mostrar(TipoEvento.Local, $"Impressão digital da chave do servidor ({CanalSeguro.Algoritmos}):");
        Mostrar(TipoEvento.Local, "  " + string.Join(' ', g[..8]));
        Mostrar(TipoEvento.Local, "  " + string.Join(' ', g[8..]));
        Mostrar(TipoEvento.Local, "Todos na sala devem ver o mesmo código. Se o seu for diferente, sua conexão foi interceptada.");
    }

    // ----------------------------------------------------------- recebimento

    private async Task ReceberAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                string? linha = await _leitor!.LerLinhaAsync(cts.Token);
                if (linha is null) return;
                if (_canal is not null)
                {
                    try { linha = _canal.Decifrar(linha); }
                    catch (CryptographicException)
                    {
                        Mostrar(TipoEvento.Aviso, "Recebida uma mensagem cifrada inválida (alterada no caminho?). Conexão encerrada por segurança.");
                        return;
                    }
                }
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
