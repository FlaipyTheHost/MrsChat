namespace MrsChat;

/// <summary>
/// O que o <see cref="ClienteChat"/> precisa da interface. Há duas implementações:
/// <see cref="TelaCheia"/> (padrão, com painéis) e <see cref="TerminalSimples"/>
/// (linha a linha, usada com --simples ou com entrada/saída redirecionada).
/// A aparência das duas vem do <see cref="Tema"/>.
/// </summary>
interface IInterfaceChat : IDisposable
{
    /// <summary>Seu nome atual (aparece no rótulo da linha de digitação).</summary>
    void DefinirNome(string nome);

    /// <summary>Mostra * no lugar do que é digitado (senha).</summary>
    bool Mascarar { get; set; }

    void Escrever(Evento evento);
    void Limpar();

    /// <summary>Lê uma linha. Retorna null se cancelado (conexão caiu) ou Ctrl+D.</summary>
    string? LerLinha(CancellationToken ct);

    void DefinirSala(string? sala, string endereco);
    void DefinirUsuarios(IReadOnlyCollection<string> nomes, string voce, bool listaCompleta);
    void DefinirConectado(bool conectado);

    /// <summary>Chave do servidor para os usuários compararem (null = conexão sem criptografia).</summary>
    void DefinirSeguranca(InfoSeguranca? info);

    /// <summary>Mostra um aviso e espera uma tecla (ex.: conexão perdida), para não sumir com a tela.</summary>
    void AguardarTecla(string aviso, CancellationToken ct);
}
