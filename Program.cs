using MrsChat;

OpcoesCliente op;
try
{
    op = OpcoesCliente.Carregar(args);
}
catch (ErroDeOpcao ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Use --ajuda para ver as opções.");
    Pausar();
    return 2;
}

if (op.MostrarAjuda) { Console.WriteLine(OpcoesCliente.TextoAjuda); return 0; }
if (op.MostrarVersao) { Console.WriteLine($"MrsChat {Protocolo.Versao}"); return 0; }

// Sem nome ou servidor (nem no .conf): pergunta, se houver alguém no terminal.
if (op.Nome is null || op.Host is null)
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Informe --nome e --servidor (ou preencha o MrsChat.conf).");
        return 2;
    }
    try
    {
        if (op.Nome is null) op.Nome = Perguntar("Seu nome: ");
        if (op.Host is null) op.DefinirServidor(Perguntar("Servidor (ex.: 192.168.0.10:9000 ou bore.pub:41234): "), "entrada");
    }
    catch (ErroDeOpcao ex)
    {
        Console.Error.WriteLine(ex.Message);
        Pausar();
        return 2;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IInterfaceChat ui = !op.Simples && TelaCheia.Disponivel(out _)
    ? new TelaCheia(op.Tema, cores: !op.SemCor)
    : new TerminalSimples(op.Tema, cores: !op.SemCor);

int codigo;
ClienteChat cliente = new(op, ui);
try
{
    codigo = await cliente.ExecutarAsync(cts.Token);
}
finally
{
    ui.Dispose(); // devolve o terminal ao normal mesmo se algo der errado
}

if (cliente.MensagemFinal is { } final)
{
    Console.ForegroundColor = codigo == 0 ? ConsoleColor.Gray : ConsoleColor.Red;
    Console.WriteLine(final);
    Console.ResetColor();
}
else if (codigo == 0)
{
    Console.WriteLine("Até logo!");
}
if (codigo == 1) Pausar();
return codigo;

static string Perguntar(string pergunta)
{
    while (true)
    {
        Console.Write(pergunta);
        string? r = Console.ReadLine()?.Trim();
        if (r is null) throw new ErroDeOpcao("Entrada encerrada.");
        if (r.Length > 0) return r;
    }
}

static void Pausar()
{
    // Mantém a janela aberta quando o .exe é aberto com duplo clique no Windows.
    if (OperatingSystem.IsWindows() && !Console.IsInputRedirected && Environment.GetCommandLineArgs().Length <= 1)
    {
        Console.WriteLine("Pressione qualquer tecla para sair...");
        Console.ReadKey(true);
    }
}
