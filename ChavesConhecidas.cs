namespace MrsChat;

/// <summary>
/// Lembra a chave de cada servidor (como o known_hosts do SSH). Na primeira conexão a
/// chave é anotada; nas seguintes, se ela mudar, alguém pode estar se passando pelo servidor.
/// Arquivo: ~/.config/MrsChat/servidores_conhecidos (Linux) ou %APPDATA%\MrsChat (Windows).
/// </summary>
static class ChavesConhecidas
{
    public enum Situacao { Nova, Conhecida, Mudou }

    public static string Caminho { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "MrsChat", "servidores_conhecidos");

    /// <param name="impressao">impressão digital completa (16 grupos)</param>
    public static Situacao Verificar(string endereco, string impressao, bool confiarSeMudou, out string? anterior)
    {
        anterior = null;
        var todas = Ler();
        if (todas.TryGetValue(endereco, out var salva))
        {
            if (salva == impressao) return Situacao.Conhecida;
            anterior = salva;
            if (!confiarSeMudou) return Situacao.Mudou;
        }
        todas[endereco] = impressao;
        Gravar(todas);
        return salva is null ? Situacao.Nova : Situacao.Mudou;
    }

    private static Dictionary<string, string> Ler()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(Caminho)) return d;
            foreach (var linha in File.ReadAllLines(Caminho))
            {
                string l = linha.Trim();
                if (l.Length == 0 || l.StartsWith('#')) continue;
                int esp = l.IndexOf(' ');
                if (esp > 0) d[l[..esp]] = l[(esp + 1)..].Trim();
            }
        }
        catch (IOException) { }
        return d;
    }

    private static void Gravar(Dictionary<string, string> d)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Caminho)!);
            var linhas = new List<string> { "# MrsChat: chaves dos servidores já visitados (endereço impressão-digital)" };
            linhas.AddRange(d.Select(kv => $"{kv.Key} {kv.Value}"));
            File.WriteAllLines(Caminho, linhas);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
