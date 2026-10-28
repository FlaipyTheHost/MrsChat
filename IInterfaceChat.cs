namespace MrsChat;

/// <summary>Um pedaço de texto com cor opcional (null = cor padrão do terminal).</summary>
readonly record struct Trecho(string Texto, ConsoleColor? Cor = null);

/// <summary>
/// O que o <see cref="ClienteChat"/> precisa da interface. Há duas implementações:
/// <see cref="TelaCheia"/> (padrão, com painéis) e <see cref="TerminalSimples"/>
/// (linha a linha, usada com --simples, em terminais pequenos ou com entrada/saída redirecionada).
/// </summary>
interface IInterfaceChat : IDisposable
{
    /// <summary>Rótulo antes do texto digitado, normalmente "Nome> ".</summary>
    string Prompt { get; set; }

    /// <summary>Mostra * no lugar do que é digitado (senha).</summary>
    bool Mascarar { get; set; }

    void Escrever(params Trecho[] trechos);
    void Escrever(string texto, ConsoleColor? cor = null) => Escrever(new Trecho(texto, cor));
    void Limpar();

    /// <summary>Lê uma linha. Retorna null se cancelado (conexão caiu) ou Ctrl+D.</summary>
    string? LerLinha(CancellationToken ct);

    void DefinirSala(string? sala, string endereco);
    void DefinirUsuarios(IReadOnlyCollection<string> nomes, string voce, bool listaCompleta);
    void DefinirConectado(bool conectado);

    /// <summary>Mostra um aviso e espera uma tecla (ex.: conexão perdida), para não sumir com a tela.</summary>
    void AguardarTecla(string aviso, CancellationToken ct);
}
