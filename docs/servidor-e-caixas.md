# Servidor da loja e caixas em rede

O **servidor da loja** (`SistemaPDV.Servidor.exe`, projeto `Pdv.Server`) roda no computador principal como serviço do
Windows (`SistemaPDV.Servidor`). É o único programa que acessa o PostgreSQL e o certificado da NFC-e. Os caixas
(`SistemaPDV.exe`) falam com ele por HTTP na rede local, porta **5080** por padrão.

## Endpoints

| Método | Caminho | Autenticação | Uso |
|---|---|---|---|
| GET | `/api/health` | nenhuma | Versão, nome do servidor, modo manutenção |
| POST | `/api/terminals/pair` | código de conexão | Conecta o computador como caixa e devolve a chave |
| GET | `/api/terminals/me` | chave do caixa | Quem sou eu (código, ID) |
| POST | `/api/rpc/{Serviço}/{Método}` | chave do caixa | Chamada de serviço da aplicação (corpo: argumentos em JSON) |

Todas as chamadas (exceto `health`) exigem o cabeçalho `X-Pdv-Version` igual à versão do servidor.
Respostas de erro têm o corpo `{ "kind": "...", "message": "..." }`:

| HTTP | `kind` | No caixa vira | Significado |
|---|---|---|---|
| 422 | `domain` | `DomainException` | Regra de negócio (mensagem pronta para o operador) |
| 401 | `unauthorized` | `TerminalNotPairedException` | Chave inválida/revogada ou código de conexão errado |
| 403 | `forbidden` | `ServerException` | Caixa tentando operar em nome de outro caixa |
| 426 | `version` | `VersionMismatchException` | Versões diferentes |
| 429 | `unauthorized` | `TerminalNotPairedException` | Muitas tentativas de código errado |
| 503 | `maintenance` | `ServerUnavailableException` | Restauração de backup em andamento |
| 500 | `server` | `ServerException` | Erro inesperado (detalhes só no log do servidor) |

Falha de conexão ou tempo esgotado no caixa vira `ServerUnavailableException` ("Sem conexão com o servidor da loja").
Prazo padrão de uma chamada: 90 s (backup e restauração: 30 min).

## Serviços expostos

Somente os listados em `RemoteApi.Services` (`Pdv.Application/Remote/RemoteApi.cs`): autenticação, usuários, produtos,
consulta de produtos, vendas, empresa, tributação, NFC-e e sua configuração, fornecedores, compras, devoluções,
relatórios, caixa, finalização, caixas conectados, backup, informações do servidor e dados de demonstração.
`TerminalRegistry` (pareamento) e `DatabaseInitializer` só existem dentro do servidor.

Para expor um serviço novo: crie-o na `Application` (ou uma interface implementada no servidor), registre na DI e
inclua em `RemoteApi.Services`. O teste `Remote_contract_has_no_overloads_entities_or_tuples` garante que a assinatura é
serializável.

## Conexão de um caixa (pareamento)

1. O técnico instala "Somente caixa" informando o endereço do computador principal (ou o caixa pergunta na primeira
   abertura).
2. O caixa confere `/api/health` (servidor no ar e mesma versão).
3. O caixa pede o **código de conexão** de 8 dígitos (`1234-5678`), visível em Retaguarda › Sistema ou com
   `SistemaPDV.Servidor.exe --codigo` no servidor. No próprio computador do servidor a conexão é direta, sem código.
4. O servidor registra (ou reaproveita, pelo nome do computador) o terminal e devolve uma **chave de acesso** de 256
   bits. O banco guarda só o SHA-256; o caixa guarda a chave cifrada com DPAPI em `connection.json`.
5. Reconectar o mesmo computador troca a chave (a antiga para de valer na hora). "Desconectar caixa" na retaguarda
   revoga a chave (exige o caixa fechado); o cache de chaves do servidor expira em até 30 s.

Proteções: 5 códigos errados de um endereço bloqueiam por 5 minutos; "Gerar novo" troca o código sem afetar caixas já
conectados.

## Um caixa só opera a si mesmo

Todo parâmetro chamado `terminalId` e todo argumento com propriedade `TerminalId` (ex.: comando de venda ou devolução)
precisa ser o terminal da chave usada; caso contrário a chamada é recusada (403).

## Versões

- Caixa e servidor precisam ter a mesma versão `major.minor.patch` (`Directory.Build.props`). O caixa desatualizado
  mostra qual versão instalar e não opera.
- O servidor aplica as migrações ao iniciar e **recusa iniciar** se o banco tiver migrações que ele não conhece
  (atualizado por versão mais nova): o log diz para instalar a versão recente.

## Quedas de rede, energia e servidor

| Situação | Comportamento |
|---|---|
| Rede cai durante a finalização | O carrinho fica travado com a mesma chave; F10 reenvia sem duplicar |
| Caixa desliga com itens no carrinho | Ao voltar, o carrinho está vazio e nada foi gravado (venda não concluída) |
| Caixa desliga durante a finalização | O diário `venda-em-andamento.json` (gravação atômica com flush) é conferido no servidor pela chave: venda gravada → aviso; não gravada → "Concluir agora?" (Sim conclui, Não descarta) |
| Servidor desligado | Mensagem "Sem conexão com o servidor da loja"; ao voltar, o caixa segue sem reiniciar |
| Servidor reinicia | Kestrel e o caixa reconectam sozinhos; chaves continuam válidas (estão no banco) |
| Restauração de backup | Novas chamadas recebem "Servidor em manutenção"; a restauração espera as chamadas em andamento |

O aceite da Etapa 5 (3 caixas, um desligado à força no meio da venda, sem duplicar nem perder) é coberto pelos testes
`Pdv.Server.Tests` e foi percorrido pela interface.

## Tarefas em segundo plano do servidor

- **Fila da NFC-e:** a cada 2 minutos transmite notas pendentes, sem resposta ou em contingência.
- **Backup diário:** ver [Manual › Backup](operacao.md#backup) e [Instalação](instalacao.md#arquivos-do-servidor).

## Configuração do servidor

| Chave (`appsettings.json`, `server.json` ou variável `Pdv__...`) | Padrão | Descrição |
|---|---|---|
| `ConnectionStrings:Pdv` | — | Conexão com o PostgreSQL (em `server.json`, senha cifrada) |
| `Pdv:Server:Port` | 5080 | Porta HTTP |
| `Pdv:Server:LocalOnly` | false | Escuta só em localhost (loja de um caixa só, testes) |
| `Pdv:Server:LocalPairingWithoutCode` | true | Caixa no próprio servidor conecta sem código |
| `Pdv:Server:DataFolder` | `%ProgramData%\SistemaPDV\Servidor` | Pasta de dados |
| `Pdv:Fiscal:Simulator` | false | SEFAZ simulada (só homologação) |
| `Pdv:Fiscal:SettingsFile` | `{pasta}\nfce.json` | Outro arquivo de configuração da NFC-e |
| `Pdv:SeedDemoData` | false | Produtos de demonstração |

## Configuração do caixa

| Chave | Descrição |
|---|---|
| `connection.json` → `Pdv:Server:Address` | Endereço do servidor (`SERVIDOR`, `192.168.0.10`, `servidor:5080`) |
| `connection.json` → `Pdv:Server:AccessKey` | Chave do caixa (DPAPI) |
| `Pdv:Terminal:Name` | Nome do terminal (padrão: nome do computador) |
| `Pdv:Receipt:Mode` / `Printer` / `PaperWidthMm` | Impressão do comprovante (`Ask`, `Always`, `Never`; impressora; 80 ou 58 mm) |
| Variável `PDV_DATA_DIR` | Outra pasta de dados (treinamento, vários caixas de teste no mesmo PC) |
