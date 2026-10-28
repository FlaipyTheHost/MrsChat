using System.Globalization;

namespace MrsChat;

/// <summary>Caracteres da moldura.</summary>
sealed record Moldura(char H, char V, char SupEsq, char SupDir, char InfEsq, char InfDir, char TEsq, char TDir, char TCima, char TBaixo)
{
    public static readonly Moldura Simples = new('─', '│', '┌', '┐', '└', '┘', '├', '┤', '┬', '┴');
    public static readonly Moldura Dupla = new('═', '║', '╔', '╗', '╚', '╝', '╠', '╣', '╦', '╩');
}

/// <summary>
/// Aparência da interface: cores, moldura, rótulos e como cada <see cref="Evento"/> vira texto.
/// Cores são inteiros (valor de ConsoleColor) e -1 significa "cor padrão do terminal".
/// </summary>
abstract class Tema
{
    public const int Padrao = -1;
    public static readonly string[] Nomes = ["militar", "ambar", "classico"];

    public static Tema? Escolher(string nome) => nome.ToLowerInvariant() switch
    {
        "militar" or "verde" or "fosforo" or "fósforo" => TemaTerminal.Verde,
        "ambar" or "âmbar" or "amber" => TemaTerminal.Ambar,
        "classico" or "clássico" or "moderno" or "colorido" => new TemaClassico(),
        _ => null,
    };

    public abstract string Id { get; }

    // ---- moldura e cores da tela cheia
    public abstract Moldura Moldura { get; }
    public abstract int Fundo { get; }
    public abstract int CorBorda { get; }
    public abstract int CorTitulo { get; }
    public abstract int CorTexto { get; }
    public abstract int CorFraca { get; }
    public abstract int CabecalhoFrente { get; }
    public abstract int CabecalhoFundo { get; }
    public abstract int BarraFrente { get; }
    public abstract int BarraFundo { get; }
    public abstract int AlertaFrente { get; }
    public abstract int AlertaFundo { get; }

    /// <summary>Sequência DECSCUSR para o formato do cursor ("" = não muda).</summary>
    public virtual string EstiloCursor => "";

    // ---- textos da tela cheia
    public abstract string Titulo(string rotulo);
    public abstract string RotuloConversa { get; }
    public abstract string RotuloMensagem { get; }
    public abstract string RotuloSenha { get; }
    public abstract string RotuloMembros(int n, bool listaCompleta);
    public abstract string Dicas(bool largo);
    public abstract string ConversaVazia { get; }
    public abstract string AvisoRolagem(int novas);
    public abstract string JanelaPequena(int w, int h);
    public virtual string? TituloAlerta => null;
    public virtual string Alerta(string texto) => texto;

    public abstract Trecho[] CabecalhoEsquerda(string? sala, string endereco);
    public abstract Trecho[] CabecalhoDireita(bool conectado, bool piscar, DateTime agora, bool largo);
    public abstract Trecho[] Membro(int indice, string nome, bool eu);

    /// <summary>Linhas da sequência de abertura (null = sem abertura).</summary>
    public virtual Trecho[][]? Abertura(string nome, string endereco) => null;

    // ---- comum às duas interfaces
    public abstract string Prompt(string nome);
    public abstract Trecho[] Formatar(Evento e);
}

/// <summary>O visual da v2.1: cores por usuário, moldura simples, cor de fundo do próprio terminal.</summary>
sealed class TemaClassico : Tema
{
    public override string Id => "classico";
    public override Moldura Moldura => Moldura.Simples;
    public override int Fundo => Padrao;
    public override int CorBorda => (int)ConsoleColor.DarkGray;
    public override int CorTitulo => (int)ConsoleColor.Cyan;
    public override int CorTexto => Padrao;
    public override int CorFraca => (int)ConsoleColor.DarkGray;
    public override int CabecalhoFrente => (int)ConsoleColor.White;
    public override int CabecalhoFundo => (int)ConsoleColor.DarkBlue;
    public override int BarraFrente => (int)ConsoleColor.Black;
    public override int BarraFundo => (int)ConsoleColor.DarkYellow;
    public override int AlertaFrente => (int)ConsoleColor.White;
    public override int AlertaFundo => (int)ConsoleColor.DarkRed;

    public override string Titulo(string rotulo) => $" {rotulo} ";
    public override string RotuloConversa => "Conversa";
    public override string RotuloMensagem => "Mensagem";
    public override string RotuloSenha => "Senha";
    public override string RotuloMembros(int n, bool completa) => completa ? $"Online ({n})" : $"Vistos ({n})";
    public override string Dicas(bool largo) => largo
        ? "Enter envia · ↑↓ histórico · PgUp/PgDn rola · Tab completa nome · Ctrl+C sai"
        : "Enter envia · PgUp/PgDn rola · Ctrl+C sai";
    public override string ConversaVazia => "Nenhuma mensagem ainda. Diga oi!";
    public override string AvisoRolagem(int novas) => novas > 0
        ? $"▼ {novas} mensagem(ns) nova(s) · PgDn ou Esc para voltar ao fim"
        : "▼ vendo mensagens antigas · PgDn ou Esc para voltar ao fim";
    public override string JanelaPequena(int w, int h) => $"Aumente a janela (mínimo {w}x{h})";

    public override Trecho[] CabecalhoEsquerda(string? sala, string endereco)
    {
        var t = new List<Trecho> { new($"MrsChat v{Protocolo.Versao}", null, true) };
        if (!string.IsNullOrEmpty(sala)) { t.Add(new("  │  ", ConsoleColor.Gray)); t.Add(new($"Sala: {sala}")); }
        if (endereco.Length > 0) { t.Add(new("  │  ", ConsoleColor.Gray)); t.Add(new(endereco)); }
        return [.. t];
    }

    public override Trecho[] CabecalhoDireita(bool conectado, bool piscar, DateTime agora, bool largo) =>
    [
        new(conectado ? "●" : "○", conectado ? ConsoleColor.Green : ConsoleColor.Red),
        new($"{(conectado ? " conectado" : " desconectado")}  {agora:HH:mm} "),
    ];

    public override Trecho[] Membro(int indice, string nome, bool eu)
    {
        var cor = Cores.DoNome(nome);
        return eu
            ? [new("● ", cor), new(nome, cor, true), new(" (você)", ConsoleColor.DarkGray)]
            : [new("● ", cor), new(nome, cor)];
    }

    public override string Prompt(string nome) => $"{nome}> ";

    public override Trecho[] Formatar(Evento e)
    {
        var hora = new Trecho(e.Hora.ToString("HH:mm") + " ", ConsoleColor.DarkGray);
        return e.Tipo switch
        {
            TipoEvento.Mensagem or TipoEvento.Propria =>
                [hora, new(e.Autor!, Cores.DoNome(e.Autor!)), new(": " + e.Texto)],
            TipoEvento.Privado => e.Extra == "para"
                ? [hora, new($"(privado para {e.Autor}) {e.Texto}", ConsoleColor.Magenta)]
                : [hora, new($"(privado) {e.Autor}: {e.Texto}", ConsoleColor.Magenta)],
            TipoEvento.Acao => [hora, new($"* {e.Autor} {e.Texto}", ConsoleColor.DarkCyan)],
            TipoEvento.Servidor => [hora, new(Protocolo.PrefixoServidor + e.Texto, ConsoleColor.DarkYellow)],
            TipoEvento.Local => [hora, new(e.Texto, ConsoleColor.DarkGray)],
            TipoEvento.Sucesso => [hora, new(e.Texto, ConsoleColor.Green)],
            TipoEvento.Aviso => [hora, new(e.Texto, ConsoleColor.DarkYellow)],
            TipoEvento.Entrou => [hora, new($"{e.Autor} conectou-se.", ConsoleColor.DarkGray)],
            TipoEvento.Saiu => [hora, new($"{e.Autor} desconectou.", ConsoleColor.DarkGray)],
            TipoEvento.Renomeou => [hora, new($"{e.Autor} agora é {e.Extra}.", ConsoleColor.DarkGray)],
            _ => [hora, new(e.Texto)],
        };
    }
}

/// <summary>
/// Terminal de fósforo dos anos 80: monocromático sobre fundo preto, moldura dupla,
/// rótulos em caixa alta no estilo teletipo, nomes como indicativos de chamada.
/// </summary>
sealed class TemaTerminal : Tema
{
    public static readonly TemaTerminal Verde = new("militar", ConsoleColor.Green, ConsoleColor.DarkGreen, ConsoleColor.Yellow);
    public static readonly TemaTerminal Ambar = new("ambar", ConsoleColor.Yellow, ConsoleColor.DarkYellow, ConsoleColor.White);

    private static readonly string[] Meses = ["JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ"];

    private readonly ConsoleColor _forte, _fraca, _alerta;

    private TemaTerminal(string id, ConsoleColor forte, ConsoleColor fraca, ConsoleColor alerta)
    {
        Id = id;
        _forte = forte;
        _fraca = fraca;
        _alerta = alerta;
    }

    public override string Id { get; }
    public override Moldura Moldura => Moldura.Dupla;
    public override int Fundo => (int)ConsoleColor.Black;
    public override int CorBorda => (int)_fraca;
    public override int CorTitulo => (int)_forte;
    public override int CorTexto => (int)_forte;
    public override int CorFraca => (int)_fraca;
    public override int CabecalhoFrente => (int)ConsoleColor.Black;
    public override int CabecalhoFundo => (int)_forte;
    public override int BarraFrente => (int)ConsoleColor.Black;
    public override int BarraFundo => (int)_alerta;
    public override int AlertaFrente => (int)ConsoleColor.White;
    public override int AlertaFundo => (int)ConsoleColor.DarkRed;
    public override string EstiloCursor => "\x1b[1 q"; // bloco piscando

    public override string Titulo(string rotulo) => $"[ {rotulo} ]";
    public override string RotuloConversa => "TRÁFEGO DE MENSAGENS";
    public override string RotuloMensagem => "TRANSMISSÃO";
    public override string RotuloSenha => "CÓDIGO DE ACESSO";
    public override string RotuloMembros(int n, bool completa) => completa ? $"OPERADORES: {n:00}" : $"VISTOS: {n:00}";
    public override string Dicas(bool largo) => largo
        ? "ENTER TRANSMITE · ↑↓ HISTÓRICO · PGUP/PGDN ROLA · TAB COMPLETA · CTRL+C ENCERRA"
        : "ENTER TRANSMITE · PGUP/PGDN · CTRL+C ENCERRA";
    public override string ConversaVazia => "AGUARDANDO TRÁFEGO...";
    public override string AvisoRolagem(int novas) => novas > 0
        ? $"▼▼ {novas:00} MENSAGEM(NS) NOVA(S) ▼▼  PGDN/ESC PARA RETORNAR"
        : "▲▲ ARQUIVO DE TRÁFEGO ▲▲  PGDN/ESC PARA RETORNAR";
    public override string JanelaPequena(int w, int h) => $"TERMINAL PEQUENO DEMAIS · MÍNIMO {w}x{h}";
    public override string? TituloAlerta => "ALERTA";
    public override string Alerta(string texto) => texto.ToUpperInvariant();

    public override Trecho[] CabecalhoEsquerda(string? sala, string endereco)
    {
        var t = new List<Trecho> { new($"■ MRS-NET v{Protocolo.Versao}", null, true) };
        if (!string.IsNullOrEmpty(sala)) t.Add(new($"  │  CANAL: {sala.ToUpperInvariant()}"));
        if (endereco.Length > 0) t.Add(new($"  │  NÓ: {endereco.ToUpperInvariant()}"));
        return [.. t];
    }

    public override Trecho[] CabecalhoDireita(bool conectado, bool piscar, DateTime agora, bool largo)
    {
        var t = new List<Trecho>
        {
            new(conectado ? (piscar ? "◉" : "○") : "×", null, true),
            new(conectado ? " LINK ATIVO" : " SEM LINK", null, true),
        };
        if (largo) t.Add(new($"  │  DTG {Dtg(agora)}"));
        t.Add(new($"  │  {agora:HH:mm:ss} ", null, true));
        return [.. t];
    }

    /// <summary>Grupo data-hora no formato militar: dia+hora+minuto, letra do fuso, mês, ano (ex.: 281432P OUT 26).</summary>
    public static string Dtg(DateTime agora)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(agora);
        int h = (int)Math.Round(offset.TotalHours);
        char fuso = h switch
        {
            0 => 'Z',
            >= 1 and <= 9 => (char)('A' + h - 1),
            >= 10 and <= 12 => (char)('K' + h - 10), // a letra J não é usada
            <= -1 and >= -12 => (char)('N' + (-h) - 1),
            _ => 'Z',
        };
        return $"{agora:ddHHmm}{fuso} {Meses[agora.Month - 1]} {agora:yy}";
    }

    public override Trecho[] Membro(int indice, string nome, bool eu) => eu
        ? [new($"{indice:00} ", _fraca), new("◆ ", ConsoleColor.White), new(nome.ToUpperInvariant(), ConsoleColor.White, true), new(" (VOCÊ)", _fraca)]
        : [new($"{indice:00} ", _fraca), new("▸ ", _forte), new(nome.ToUpperInvariant(), _forte)];

    public override string Prompt(string nome) => $"{nome.ToUpperInvariant()} ▸ ";

    public override Trecho[] Formatar(Evento e)
    {
        var hora = new Trecho($"[{e.Hora:HH:mm:ss}] ", _fraca);
        var sep = new Trecho(" » ", _fraca);
        string Autor() => (e.Autor ?? "?").ToUpperInvariant();
        string Alto(string s) => s.ToUpperInvariant();

        return e.Tipo switch
        {
            TipoEvento.Mensagem => [hora, new(Autor(), _forte, true), sep, new(e.Texto, _forte)],
            TipoEvento.Propria => [hora, new(Autor(), ConsoleColor.White, true), sep, new(e.Texto, _forte)],
            TipoEvento.Privado => e.Extra == "para"
                ? [hora, new("[PRIV] PARA ", ConsoleColor.White), new(Autor(), ConsoleColor.White, true), sep, new(e.Texto, ConsoleColor.White)]
                : [hora, new("[PRIV] DE ", ConsoleColor.White), new(Autor(), ConsoleColor.White, true), sep, new(e.Texto, ConsoleColor.White)],
            TipoEvento.Acao => [hora, new($"* {Autor()} {e.Texto}", _fraca)],
            TipoEvento.Servidor => [hora, new("SISTEMA", _alerta, true), sep, new(Alto(e.Texto), _alerta)],
            TipoEvento.Local => [hora, new("··· " + Alto(e.Texto), _fraca)],
            TipoEvento.Sucesso => [hora, new("[ OK ] ", _forte, true), new(Alto(e.Texto), _forte)],
            TipoEvento.Aviso => [hora, new("[ !! ] ", _alerta, true), new(Alto(e.Texto), _alerta)],
            TipoEvento.Entrou => [hora, new($"▲ {Autor()} ENTROU NA REDE", _fraca)],
            TipoEvento.Saiu => [hora, new($"▼ {Autor()} SAIU DA REDE", _fraca)],
            TipoEvento.Renomeou => [hora, new($"◇ {Autor()} AGORA É {(e.Extra ?? "?").ToUpperInvariant()}", _fraca)],
            _ => [hora, new(e.Texto, _forte)],
        };
    }

    public override Trecho[][] Abertura(string nome, string endereco)
    {
        Trecho[] L(string s, ConsoleColor? c = null, bool n = false) => [new(s, c ?? _forte, n)];
        Trecho[] Item(string rotulo, string valor) => [new(rotulo.PadRight(28, '.') + " ", _fraca), new(valor, _forte, true)];
        return
        [
            L("███╗   ███╗██████╗ ███████╗      ███╗   ██╗███████╗████████╗", _forte, true),
            L("████╗ ████║██╔══██╗██╔════╝      ████╗  ██║██╔════╝╚══██╔══╝", _forte, true),
            L("██╔████╔██║██████╔╝███████╗█████╗██╔██╗ ██║█████╗     ██║   ", _forte, true),
            L("██║╚██╔╝██║██╔══██╗╚════██║╚════╝██║╚██╗██║██╔══╝     ██║   ", _forte, true),
            L("██║ ╚═╝ ██║██║  ██║███████║      ██║ ╚████║███████╗   ██║   ", _forte, true),
            L("╚═╝     ╚═╝╚═╝  ╚═╝╚══════╝      ╚═╝  ╚═══╝╚══════╝   ╚═╝   ", _fraca),
            L(""),
            L($"REDE DE COMUNICAÇÃO MRS · TERMINAL v{Protocolo.Versao}", _fraca),
            L(new string('─', 60), _fraca),
            Item("AUTOTESTE DE MEMÓRIA", "640K OK"),
            Item("INICIALIZANDO MODEM", "OK"),
            Item("CARREGANDO TABELA DE ROTAS", "OK"),
            Item("OPERADOR", nome.ToUpperInvariant()),
            Item("DISCANDO NÓ", endereco.ToUpperInvariant()),
        ];
    }
}
