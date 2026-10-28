# <img src="icon.ico" width="40" height="auto"> MrsChat

[![Release](https://img.shields.io/github/v/release/FlaipyTheHost/MrsChat?label=release)](https://github.com/FlaipyTheHost/MrsChat/releases/latest)
[![Licença](https://img.shields.io/github/license/FlaipyTheHost/MrsChat)](LICENSE)

Cliente de chat para o terminal, com cara de central de comunicação dos anos 80 e mensagens cifradas.
Conversa em salas hospedadas pelo [**MrsChatServer**](https://github.com/FlaipyTheHost/MrsChatServer), na rede local ou pela internet.

O projeto nasceu de uma cena de **The Incredible Hulk (2008)**, em que Bruce Banner, escondido numa favela do Rio, troca mensagens com o misterioso "Mr. Blue". A ideia era aprender na prática como funciona a comunicação cliente-servidor com sockets de rede, e o app foi crescendo a partir daí.

<img src="captura.gif" alt="MrsChat em funcionamento">

## Recursos

- **Tela cheia em estilo terminal de fósforo verde**: histórico com rolagem, painel de operadores, caixa de mensagem e cabeçalho com sala, endereço, status do link e relógio. Tem também um tema âmbar e um clássico colorido.
- **Mensagens cifradas** com AES-256-GCM até o servidor, com troca de chaves ECDH P-256 assinada pelo servidor.
- **Painel de verificação** com a impressão digital da chave do servidor, para todos na sala conferirem que veem o mesmo código.
- **Memória de chaves**: o cliente avisa e recusa a conexão se a chave de um servidor conhecido mudar, como o SSH faz.
- **Funciona pela internet**, mesmo atrás de CGNAT, quando o servidor usa `--publico`. Para o cliente, é só um endereço como `bore.pub:41234`.
- Mensagens privadas, `/me`, troca de nome, histórico com ↑/↓, Tab para completar nomes.
- **Não precisa do .NET instalado**: os pacotes da release trazem tudo.

<img src="captura2.gif" alt="Captura do MrsChat">

## Instalação

Baixe o pacote do seu sistema na [página de releases](https://github.com/FlaipyTheHost/MrsChat/releases/latest).

| Sistema | Arquivo | Como instalar |
|---|---|---|
| **Windows** 10/11 | `MrsChat-<versão>-win-x64.zip` | Extraia e rode `MrsChat.exe` no Windows Terminal |
| **Ubuntu, Debian, Mint** | `mrschat_<versão>_amd64.deb` | `sudo apt install ./mrschat_<versão>_amd64.deb` |
| **Fedora, RHEL, openSUSE** | `mrschat-<versão>-1.x86_64.rpm` | `sudo dnf install ./mrschat-<versão>-1.x86_64.rpm` |
| **Outras distros Linux** | `mrschat-<versão>-linux-x64.tar.gz` | `tar xzf mrschat-*.tar.gz && cd mrschat-*/ && ./MrsChat` |

Os pacotes `.deb` e `.rpm` instalam o comando `mrschat`. Para conferir se o download chegou íntegro, use o `SHA256SUMS.txt` da release: `sha256sum -c SHA256SUMS.txt`.

## Primeiros passos

```bash
mrschat --nome Banner --servidor 192.168.0.10:9000      # na rede local
mrschat --nome Banner --servidor bore.pub:41234         # pela internet
```

No Windows, use `MrsChat.exe` no lugar de `mrschat`. Se faltar o nome ou o servidor, o programa pergunta.

Na primeira conexão com um servidor, o painel **VERIFICAÇÃO** mostra a chave dele (ex.: `94C5 F857 C067 1AE7…`). **Confira esse código com quem administra a sala por outro meio**, como uma ligação ou o WhatsApp. Ele deve ser igual ao que aparece no painel do servidor e igual para todos na sala.

## Uso

| Opção | Descrição |
|---|---|
| `-n`, `--nome <nome>` | Seu nome na sala |
| `-s`, `--servidor <endereço>` | `host:porta`, `[IPv6]:porta` ou `tcp://host:porta` (porta padrão: 9000) |
| `--senha <senha>` | Senha da sala, se houver; sem ela, o programa pergunta |
| `-t`, `--tema <nome>` | `militar` (padrão), `ambar` ou `classico` |
| `--simples` | Interface linha a linha, sem painéis (usada também quando a saída é redirecionada) |
| `--sem-cor` | Desliga as cores |
| `--confiar-nova-chave` | Aceita a chave do servidor mesmo que tenha mudado (só depois de confirmar com o administrador) |
| `--sem-cripto` | Conecta sem criptografia, apenas para servidores antigos (v2.2 ou anteriores) |
| `-h`, `--ajuda` / `-v`, `--versao` | Ajuda e versão |

A forma antiga `MrsChat <nome> <servidor:porta>` continua funcionando.

### Comandos no chat

| Comando | Descrição |
|---|---|
| `/usuarios` | Quem está online |
| `/msg <nome> <texto>` | Mensagem privada |
| `/nick <novo nome>` | Troca seu nome |
| `/me <ação>` | Ação, ex.: `/me acenou` |
| `/chave` | Impressão digital completa da chave do servidor |
| `/limpar` | Limpa a conversa |
| `/ajuda` | Lista os comandos |
| `/sair` | Sai (ou Ctrl+C) |

### Teclas

| Tecla | Ação |
|---|---|
| Enter | Envia |
| ↑ / ↓ | Mensagens enviadas antes |
| PgUp / PgDn | Rola o histórico |
| Esc | Limpa a mensagem; com ela vazia, volta ao fim da conversa |
| Tab | Completa o nome de um operador (de novo para o próximo) |
| ← → Home End, Ctrl+W | Editam a mensagem |
| Ctrl+L | Redesenha a tela |

### Arquivo de configuração

O `MrsChat.conf` guarda valores padrão, e as opções da linha de comando têm prioridade sobre ele. Ele é procurado nesta ordem: na pasta atual, em `~/.config/MrsChat/` (no Windows, `%APPDATA%\MrsChat\`) e na pasta do programa.

```ini
nome=MrBlue
servidor=bore.pub:41234
senha=
tema=militar
```

Quem instalou pelo `.deb` ou `.rpm` encontra um exemplo em `/usr/share/doc/mrschat/MrsChat.conf.exemplo`.

## Segurança

- Tudo o que você envia vai cifrado até o servidor: mensagens, nome e senha da sala. Quem está no caminho (Wi-Fi, provedor, o relay `bore.pub`) não consegue ler, alterar, repetir nem reordenar nada. Uma mensagem adulterada derruba a conexão.
- **Não é ponta a ponta.** O servidor decifra para repassar as mensagens. O painel dele não mostra o conteúdo, mas quem controla o servidor pode, tecnicamente, lê-lo. Use salas de quem você confia.
- As chaves dos servidores já visitados ficam em `~/.config/MrsChat/servidores_conhecidos` (no Windows, `%APPDATA%\MrsChat\`). Se a chave de um endereço mudar, o cliente recusa a conexão e mostra a chave antiga e a nova.
- Comparar o código só dentro da sala não basta contra quem intercepta todos ao mesmo tempo: confira com o administrador por outro canal.

## Compatibilidade

| Cliente | Servidor | Resultado |
|---|---|---|
| v2.3 | v2.3 | Cifrado, com todos os recursos |
| v2.3 | v2.0 a v2.2 | Recusa e sugere atualizar o servidor (ou `--sem-cripto`) |
| v2.3 | v1.0 | Só com `--sem-cripto`; sem painel de operadores completo |
| v1.0 a v2.2 | v2.3 | Recusado, a menos que o servidor use `--aceitar-sem-cripto` |

## Compilando

Precisa do [SDK do .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) ou mais novo.

```bash
git clone https://github.com/FlaipyTheHost/MrsChat.git
cd MrsChat
dotnet run -c Release -- --nome Banner --servidor localhost:9000
```

Para gerar um executável único, sem depender do .NET instalado:

```bash
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publicar/linux-x64
```

### Publicando uma release

O workflow [`.github/workflows/release.yml`](.github/workflows/release.yml) gera o `.zip` do Windows, o `.tar.gz`, o `.deb` e o `.rpm`, testa os executáveis e cria a release com os arquivos e o `SHA256SUMS.txt`:

```bash
git tag v2.3.0
git push origin v2.3.0
```

Ele também pode ser disparado à mão em **Actions → Release → Run workflow**. Nesse caso, os pacotes ficam disponíveis como artefato da execução, sem criar release. O empacotamento é feito pelo [`empacotamento/empacotar.sh`](empacotamento/empacotar.sh), que também roda localmente.

## Licença

[GPL-3.0](LICENSE).
