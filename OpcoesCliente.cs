namespace MrsChat;

sealed class OpcoesCliente
{
    public const string ArquivoConfiguracao = "MrsChat.conf";
    public const int PortaPadrao = 9000;

    public string? Nome { get; set; }
    public string? Host { get; set; }
    public int Porta { get; set; } = PortaPadrao;
    public string? Senha { get; set; }
    public bool SemCor { get; set; }
    public bool Simples { get; set; }
    public bool SemCripto { get; set; }
    public bool ConfiarNovaChave { get; set; }
    public Tema Tema { get; set; } = TemaTerminal.Verde;
    public bool MostrarAjuda { get; set; }
    public bool MostrarVersao { get; set; }

    public string Endereco => Host is not null && Host.Contains(':') ? $"[{Host}]:{Porta}" : $"{Host}:{Porta}";

    /// <summary>Padrões &lt; MrsChat.conf &lt; argumentos.</summary>
    public static OpcoesCliente Carregar(string[] args)
    {
        var op = new OpcoesCliente();
        op.AplicarArquivo();
        op.AplicarArgumentos(args);
        return op;
    }

    private void AplicarArquivo()
    {
        // Diretório atual, depois ~/.config/MrsChat (útil quando instalado por pacote), depois a pasta do programa.
        string configUsuario = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MrsChat");
        string? caminho = new[] { Directory.GetCurrentDirectory(), configUsuario, AppContext.BaseDirectory }
            .Select(d => Path.Combine(d, ArquivoConfiguracao))
            .FirstOrDefault(File.Exists);
        if (caminho is null) return;

        foreach (var linhaBruta in File.ReadAllLines(caminho))
        {
            string linha = linhaBruta.Trim();
            if (linha.Length == 0 || linha.StartsWith('#')) continue;
            int igual = linha.IndexOf('=');
            if (igual <= 0) continue;

            string chave = linha[..igual].Trim().ToLowerInvariant();
            string valor = linha[(igual + 1)..].Trim();
            switch (chave)
            {
                case "nome": Nome = Vazio(valor); break;
                case "servidor": if (valor.Length > 0) DefinirServidor(valor, caminho); break;
                case "senha": Senha = Vazio(valor); break;
                case "cores": SemCor = valor.ToLowerInvariant() is "nao" or "não" or "false" or "0" or "off"; break;
                case "cripto": SemCripto = valor.ToLowerInvariant() is "nao" or "não" or "false" or "0" or "off"; break;
                case "interface": Simples = valor.Equals("simples", StringComparison.OrdinalIgnoreCase); break;
                case "tema": if (valor.Length > 0) Tema = LerTema(valor, caminho); break;
            }
        }
    }

    private void AplicarArgumentos(string[] args)
    {
        var posicionais = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Valor()
            {
                if (i + 1 >= args.Length) throw new ErroDeOpcao($"A opção {a} precisa de um valor.");
                return args[++i];
            }

            switch (a)
            {
                case "-h" or "--ajuda" or "--help" or "/?": MostrarAjuda = true; break;
                case "-v" or "--versao" or "--version": MostrarVersao = true; break;
                case "-n" or "--nome": Nome = Valor(); break;
                case "-s" or "--servidor": DefinirServidor(Valor(), a); break;
                case "--senha": Senha = Valor(); break;
                case "--sem-cor" or "--no-color": SemCor = true; break;
                case "--simples": Simples = true; break;
                case "--sem-cripto": SemCripto = true; break;
                case "--confiar-nova-chave": ConfiarNovaChave = true; break;
                case "-t" or "--tema": Tema = LerTema(Valor(), a); break;
                default:
                    if (a.StartsWith("--")) throw new ErroDeOpcao($"Opção desconhecida: {a}");
                    posicionais.Add(a);
                    break;
            }
        }

        // Forma da v1: MrsChat <nome> <ip:porta>
        if (posicionais.Count > 2) throw new ErroDeOpcao("Argumentos demais. Uso: MrsChat <nome> <servidor:porta>");
        if (posicionais.Count >= 1) Nome = posicionais[0];
        if (posicionais.Count == 2) DefinirServidor(posicionais[1], "argumento");
    }

    /// <summary>
    /// Aceita "host", "host:porta", "[ipv6]:porta" e também "tcp://host:porta",
    /// que é como alguns serviços de túnel (Pinggy, ngrok) mostram o endereço.
    /// </summary>
    public void DefinirServidor(string valor, string origem)
    {
        string v = valor.Trim();
        int esquema = v.IndexOf("://", StringComparison.Ordinal);
        if (esquema >= 0) v = v[(esquema + 3)..];
        v = v.TrimEnd('/');

        string host;
        int porta = PortaPadrao;

        if (v.StartsWith('['))
        {
            int fecha = v.IndexOf(']');
            if (fecha < 0) throw new ErroDeOpcao($"{origem}: endereço inválido \"{valor}\".");
            host = v[1..fecha];
            string resto = v[(fecha + 1)..];
            if (resto.StartsWith(':')) porta = LerPorta(resto[1..], origem, valor);
        }
        else if (v.Count(c => c == ':') == 1)
        {
            int dp = v.IndexOf(':');
            host = v[..dp];
            porta = LerPorta(v[(dp + 1)..], origem, valor);
        }
        else
        {
            host = v; // nome, IPv4 sem porta, ou IPv6 sem colchetes
        }

        if (host.Length == 0) throw new ErroDeOpcao($"{origem}: endereço inválido \"{valor}\".");
        Host = host;
        Porta = porta;
    }

    private static int LerPorta(string s, string origem, string original)
    {
        if (!int.TryParse(s, out int p) || p < 1 || p > 65535)
            throw new ErroDeOpcao($"{origem}: porta inválida em \"{original}\".");
        return p;
    }

    private static Tema LerTema(string v, string origem) =>
        Tema.Escolher(v) ?? throw new ErroDeOpcao($"{origem}: tema desconhecido \"{v}\" (use: {string.Join(", ", Tema.Nomes)}).");

    private static string? Vazio(string v) => v.Length == 0 ? null : v;

    public static string TextoAjuda => $"""
        MrsChat v{Protocolo.Versao} - cliente de chat para servidores MrsChatServer

        Uso:
          MrsChat [opções]
          MrsChat <nome> <servidor[:porta]>      (forma antiga, ainda aceita)

        Opções:
          -n, --nome <nome>          Seu nome na sala
          -s, --servidor <endereço>  Servidor, ex.: 192.168.0.10:9000 ou bore.pub:41234
              --senha <senha>        Senha da sala, se houver
          -t, --tema <nome>          militar (padrão, fósforo verde), ambar ou classico
              --sem-cor              Desliga as cores
              --sem-cripto           Conecta sem criptografia (só para servidores antigos)
              --confiar-nova-chave   Aceita a chave do servidor mesmo que tenha mudado
              --simples              Interface linha a linha, sem painéis (a tela cheia é o padrão)
          -h, --ajuda                Mostra esta ajuda
          -v, --versao               Mostra a versão

        Sem nome ou servidor, eles são lidos do {ArquivoConfiguracao} ou perguntados na hora.

        Arquivo {ArquivoConfiguracao}:
          nome=MrBlue
          servidor=bore.pub:41234
          senha=
          interface=tela-cheia      (ou: simples)
          tema=militar              (ou: ambar, classico)

        Comandos dentro do chat:
          /usuarios  /msg <nome> <texto>  /nick <nome>  /me <ação>  /chave  /limpar  /ajuda  /sair

        Criptografia: tudo vai cifrado (AES-256-GCM) até o servidor. A impressão digital da chave do
        servidor aparece no painel VERIFICAÇÃO; todos na sala devem ver o mesmo código.
        As chaves vistas ficam em {ChavesConhecidas.Caminho}.

        Teclas na tela cheia:
          Enter envia · ↑/↓ histórico · PgUp/PgDn rola · Tab completa nomes · Esc limpa · Ctrl+L redesenha
        """;
}

sealed class ErroDeOpcao(string mensagem) : Exception(mensagem);
