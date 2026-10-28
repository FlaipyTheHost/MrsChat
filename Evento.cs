namespace MrsChat;

/// <summary>O que aconteceu no chat, sem decidir aparência. O <see cref="Tema"/> decide como mostrar.</summary>
enum TipoEvento
{
    /// <summary>Mensagem de outro usuário (Autor, Texto).</summary>
    Mensagem,
    /// <summary>Mensagem que você enviou (Autor, Texto).</summary>
    Propria,
    /// <summary>Mensagem privada. Extra = "de" (recebida) ou "para" (enviada).</summary>
    Privado,
    /// <summary>/me: Autor fez Texto.</summary>
    Acao,
    /// <summary>Mensagem do servidor (sem o prefixo "[servidor] ").</summary>
    Servidor,
    /// <summary>Informação do próprio cliente (conectando, ajuda...).</summary>
    Local,
    Sucesso,
    Aviso,
    Entrou,
    Saiu,
    /// <summary>Autor trocou de nome para Extra.</summary>
    Renomeou,
    /// <summary>Linha que não seguiu nenhum formato conhecido.</summary>
    Texto,
}

readonly record struct Evento(TipoEvento Tipo, string Texto = "", string? Autor = null, string? Extra = null)
{
    public DateTime Hora { get; init; } = DateTime.Now;
}

/// <summary>Um pedaço de texto com cor opcional (null = cor padrão do tema/terminal).</summary>
readonly record struct Trecho(string Texto, ConsoleColor? Cor = null, bool Negrito = false);
