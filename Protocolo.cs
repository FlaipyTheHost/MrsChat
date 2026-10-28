namespace MrsChat;

/// <summary>Textos fixos combinados com o MrsChatServer v2 (o servidor v1 nunca os envia).</summary>
static class Protocolo
{
    public const string Versao = "2.0";
    public const string PrefixoServidor = "[servidor] ";
    public const string PedidoSenha = PrefixoServidor + "Esta sala exige senha. Digite a senha:";
    public const string PrefixoSeuNome = PrefixoServidor + "Seu nome nesta sala é: ";
    public const int TamanhoMaximoLinha = 4096;
}
