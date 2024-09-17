using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static StringBuilder logChat = new StringBuilder();

    static async Task Main(string[] args)
    {
        string nomeUsuario;
        string enderecoServidor;
        int porta;

        if (args.Length >= 2) //Argumento
        {
            nomeUsuario = args[0];
            string[] partesServidor = args[1].Split(':');
            enderecoServidor = partesServidor[0];
            porta = int.Parse(partesServidor[1]);
        }
        else //Arquivo
        {
            if (!LerArquivoConfiguracao(out nomeUsuario, out enderecoServidor, out porta))
            {
                Console.WriteLine("Não foi possível ler o arquivo de configuração.");
                Console.ReadKey();
                return;
            }
        }

        TcpClient cliente = new TcpClient();

        try
        {
            // Cabeçalho
            Console.Clear();
            Console.WriteLine("MrsChat v1.0");
            Console.WriteLine("--------------------------------------------");

            Console.WriteLine("Conectando ao servidor...");
            await cliente.ConnectAsync(enderecoServidor, porta);
            Console.WriteLine("Conectado ao servidor!");

            NetworkStream stream = cliente.GetStream();

            // Envia o nome do usuário para o servidor
            byte[] bytesNome = Encoding.UTF8.GetBytes(nomeUsuario + "\n");
            await stream.WriteAsync(bytesNome, 0, bytesNome.Length);

            AdicionarMensagemAoChat($"{nomeUsuario} conectou-se!");

            _ = Task.Run(() => ReceberMensagens(stream)); // Inicia a tarefa para receber mensagens

            // Enviador de mensagens
            while (true)
            {
                AtualizarTela(); // Atualiza a tela a cada loop

                // Entrada de Texto
                string mensagem = Console.ReadLine();
                if (mensagem.ToLower() == "exit") break;

                byte[] msg = Encoding.UTF8.GetBytes(mensagem + "\n");
                await stream.WriteAsync(msg, 0, msg.Length);

                AdicionarMensagemAoChat($"{nomeUsuario}: {mensagem}"); // Adiciona a mensagem ao chat local
            }
        }
        catch (SocketException)
        {
            Console.WriteLine("Não foi possível se conectar ao servidor. Verifique a conexão e tente novamente.");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ocorreu um erro: {ex.Message}");
            Console.ReadKey();
        }
        finally
        {
            cliente.Close();
        }
    }

    private static bool LerArquivoConfiguracao(out string nomeUsuario, out string enderecoServidor, out int porta)
    {
        nomeUsuario = null;
        enderecoServidor = null;
        porta = 0;

        try
        {
            var linhasConfiguracao = File.ReadAllLines("MrsChat.conf");
            foreach (var linha in linhasConfiguracao)
            {
                if (linha.StartsWith("nome="))
                {
                    nomeUsuario = linha.Substring(5);
                }
                else if (linha.StartsWith("servidor="))
                {
                    var infoServidor = linha.Substring(9).Split(':');
                    enderecoServidor = infoServidor[0];
                    if (infoServidor.Length > 1)
                    {
                        porta = int.Parse(infoServidor[1]);
                    }
                    else
                    {
                        porta = 9000; // padrao
                    }
                }
            }

            return nomeUsuario != null && enderecoServidor != null && porta > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao ler o arquivo de configuração: {ex.Message}");
            return false;
        }
    }

    private static void AtualizarTela()
    {
        Console.Clear();
        // Cabeçalho
        Console.WriteLine("MrsChat v1.0");
        Console.WriteLine("--------------------------------------------");

        // Chat
        RenderizarLogChat();
        Console.WriteLine("--------------------------------------------");
        Console.WriteLine(); // è preciso ter uma linha em branco para entrada de texto
    }

    private static void RenderizarLogChat()
    {
        var mensagens = logChat.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var mensagem in mensagens)
        {
            var cor = ObterCorDaMensagem(mensagem);
            Console.ForegroundColor = cor;
            Console.WriteLine(mensagem);
            Console.ResetColor();
        }
    }

    private static void AdicionarMensagemAoChat(string mensagem)
    {
        logChat.AppendLine(mensagem);
    }

    private static ConsoleColor ObterCorDaMensagem(string mensagem)
    {
        if (mensagem.Contains("Blue"))
            return ConsoleColor.Blue;
        if (mensagem.Contains("Red"))
            return ConsoleColor.Red;
        if (mensagem.Contains("Purple"))
            return ConsoleColor.Magenta;
        if (mensagem.Contains("Green"))
            return ConsoleColor.Green;
        if (mensagem.Contains("Yellow"))
            return ConsoleColor.Yellow;
        if (mensagem.Contains("Cyan"))
            return ConsoleColor.Cyan;
        if (mensagem.Contains("White"))
            return ConsoleColor.White;
        if (mensagem.Contains("Gray"))
            return ConsoleColor.Gray;
        if (mensagem.Contains("Black"))
            return ConsoleColor.Black;

        return ConsoleColor.Gray; // Cor padrão
    }

    private static async Task ReceberMensagens(NetworkStream stream)
    {
        byte[] buffer = new byte[1024];
        StringBuilder construtorMensagem = new StringBuilder();
        while (true)
        {
            try
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;  

                string dadosRecebidos = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                construtorMensagem.Append(dadosRecebidos);

                // Processa as mensagens completas
                string mensagem;
                while ((mensagem = ExtrairMensagem(construtorMensagem)) != null)
                {
                    AdicionarMensagemAoChat(mensagem); 
                    AtualizarTela(); 
                }
            }
            catch
            {
                break; // se houver um erro na leitura encerra a leitura
            }
        }
    }

    private static string ExtrairMensagem(StringBuilder construtorMensagem)
    {
        string dados = construtorMensagem.ToString();
        int indice = dados.IndexOf('\n');

        if (indice >= 0)
        {
            string mensagem = dados.Substring(0, indice).Trim();
            construtorMensagem.Remove(0, indice + 1); 
            return mensagem;
        }

        return null; 
    }
}
