# <img src="icon.ico" width="48" height="auto"> MrsChat

**MrsChat** é um aplicativo cliente que tem como objetivo se conectar a uma sala hospedada por um servidor MRS, pode ser adiquirido [neste meu outro repositório](https://github.com/FlaipyTheHost/MrsChatServer).

Criei este projeto inspirado em uma cena do filme **The Incredible Hulk (2008)** onde Bruce Banner se comunica com "Mr.Blue" de uma favela do Rio. Além da vontade de aprender como funciona a tecnologia cliente servidor na prática, com uso de sockets de rede.
# <img src="captura.gif">

Captura do funcionamento de **MrsChat**:
# <img src="captura2.gif">

## Novidades da v2.0

- **Conexão pela internet**: basta usar o endereço público que o servidor mostra com `--publico` (ex.: `bore.pub:41234`).
- **Interface de linha de comando** com opções (`--nome`, `--servidor`, `--senha`) e `--ajuda`. Se faltar algo, o programa pergunta.
- **Terminal sem piscar**: as mensagens chegam acima da linha de digitação sem apagar o que você está escrevendo.
- Histórico com ↑/↓, horário nas mensagens, cor por usuário e senha digitada com `*`.
- **Comandos**: `/usuarios`, `/msg`, `/nick`, `/me`, `/limpar`, `/ajuda`, `/sair`.
- Aceita endereços `host:porta`, `[IPv6]:porta` e `tcp://host:porta` (formato de alguns túneis).
- Continua **compatível com o MrsChatServer v1.0** (os comandos de sala exigem o servidor v2).

## Instalação

É necessário ter o **.NET 8 ou mais novo** (o app usa a versão mais nova que encontrar, então o .NET 10 também serve).

**Windows:** instale o runtime pelo [site da Microsoft](https://dotnet.microsoft.com/pt-br/download/dotnet/8.0) e execute o `MrsChat.exe` das [releases](https://github.com/FlaipyTheHost/MrsChat/releases/).

**Ubuntu/Mint/Debian:**
```
sudo apt install dotnet-runtime-8.0
```

**Fedora:**
```
sudo dnf install dotnet-runtime-8.0
```

## Uso

```
unzip MrsChat_x64_Release_*.zip
dotnet MrsChat.dll --nome MrBlue --servidor 192.168.0.10:9000
```

| Opção | Descrição |
|---|---|
| `-n`, `--nome <nome>` | Seu nome na sala |
| `-s`, `--servidor <endereço>` | Ex.: `192.168.0.10:9000` ou `bore.pub:41234` |
| `--senha <senha>` | Senha da sala, se houver (sem ela, o programa pergunta) |
| `--sem-cor` | Desliga as cores |
| `-h`, `--ajuda` | Mostra a ajuda |

A forma antiga `dotnet MrsChat.dll <nome_usuário> <ip:porta>` continua funcionando, assim como rodar sem parâmetros usando o `MrsChat.conf`:

```
nome=MrGreen
servidor=localhost:9000
senha=
```

## Comandos no chat

| Comando | Descrição |
|---|---|
| `/usuarios` | Quem está online |
| `/msg <nome> <texto>` | Mensagem privada |
| `/nick <novo nome>` | Troca seu nome |
| `/me <ação>` | Ex.: `/me acenou` |
| `/limpar` | Limpa a tela |
| `/sair` | Sai do chat (ou Ctrl+C) |
