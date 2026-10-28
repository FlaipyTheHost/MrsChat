namespace MrsChat;

static class Cores
{
    private static readonly ConsoleColor[] Paleta =
    [
        ConsoleColor.Cyan, ConsoleColor.Green, ConsoleColor.Yellow, ConsoleColor.Magenta,
        ConsoleColor.Blue, ConsoleColor.Red, ConsoleColor.DarkCyan, ConsoleColor.DarkGreen,
        ConsoleColor.DarkYellow, ConsoleColor.DarkMagenta,
    ];

    /// <summary>
    /// Como na v1, nomes com uma cor (MrBlue, MrRed...) aparecem nessa cor.
    /// Os outros ganham uma cor fixa calculada a partir do nome.
    /// </summary>
    public static ConsoleColor DoNome(string nome)
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
}
