# Sistema PDV

Ponto de venda para Windows em **.NET 10 (LTS)**, com WPF, PostgreSQL e EF Core 10. Vários caixas em rede usam um
servidor central da loja (ASP.NET Core) — os caixas não acessam o banco.
**Documentação completa: [docs/](docs/README.md)** (arquitetura, regras de negócio, servidor e caixas, fiscal,
manual de operação, instalação, segurança, desenvolvimento e histórico). Etapas: [docs/ROADMAP.md](docs/ROADMAP.md).
Este repositório implementa o início do MVP descrito no planejamento do produto (V0.1 → V0.3).

## Stack

| Camada | Tecnologia |
|---|---|
| Runtime / linguagem | .NET 10 LTS, C# 14 |
| Interface | WPF + CommunityToolkit.Mvvm 8.4 |
| Hospedagem / DI / config | Microsoft.Extensions.Hosting 10 (`appsettings.json`) |
| Servidor da loja | ASP.NET Core 10 (Kestrel) como serviço do Windows |
| Banco | PostgreSQL 17 (provedor Npgsql) |
| Acesso a dados | Entity Framework Core 10 + migrações |
| Logs técnicos | Serilog (arquivo diário em `%ProgramData%\SistemaPDV\logs`) |
| Testes | xUnit v3 no Microsoft.Testing.Platform (`dotnet test`) |
| Solução | formato `.slnx` |

## Estrutura

```
src/
  Pdv.Domain           Entidades e regras de negócio puras (sem dependências)
  Pdv.Application      Casos de uso: login, caixa, finalização de venda, busca de produtos
  Pdv.Infrastructure   EF Core/PostgreSQL, migrações, hash de senha, backup/restauração (pg_dump)
  Pdv.Fiscal           NFC-e (DFe.NET): XML, assinatura, SEFAZ
  Pdv.Server           Servidor da loja: único com acesso ao banco e ao certificado; backups; fila da NFC-e
  Pdv.Client           Cliente HTTP do caixa, diário da venda e conexão (chave do caixa)
  Pdv.Desktop          App WPF (frente de caixa e retaguarda)
tests/
  Pdv.Domain.Tests          Totais, arredondamento, descontos, troco, caixa, estoque
  Pdv.Infrastructure.Tests  Banco real (PostgreSQL): idempotência, concorrência, atomicidade, backup
  Pdv.Fiscal.Tests          Chave de acesso, XML e assinatura da NFC-e
  Pdv.Server.Tests          Servidor em memória: conexão de caixas, 3 caixas simultâneos, quedas, backup/restauração
```

Dependências: `Desktop → Client → Application → Domain`; `Server → Application + Infrastructure + Fiscal`.

### Servidor da loja e caixas em rede

- O caixa chama os serviços da aplicação no servidor (`POST /api/rpc/{Serviço}/{Método}`, argumentos em JSON).
  No código continua escrito como chamada local: `runner.RunAsync<CashService, Result<X>>(c => c.OpenAsync(...))`.
  Só os serviços listados em `RemoteApi.Services` são expostos.
- **Conexão do caixa:** na primeira abertura o caixa pede o endereço do servidor e o **código de conexão**
  (Retaguarda › Sistema, ou `SistemaPDV.Servidor.exe --codigo` no servidor). O servidor devolve uma chave própria do
  caixa (256 bits; o banco guarda só o SHA-256; no caixa fica cifrada com DPAPI). 5 códigos errados bloqueiam o
  endereço por 5 minutos. No próprio computador do servidor a conexão é direta. Caixas podem ser desconectados na
  retaguarda.
- Um caixa só opera a própria gaveta: parâmetros `terminalId` e comandos com `TerminalId` são conferidos com a chave.
- **Versão:** caixa e servidor precisam estar na mesma versão (`X-Pdv-Version`); o servidor recusa iniciar com um
  banco atualizado por versão mais nova.
- **Queda de energia/rede:** a venda é gravada num diário local (`venda-em-andamento.json`, gravação atômica) antes de
  ir ao servidor. Na volta, o caixa confere pela chave de idempotência: se a venda foi gravada, avisa; se não, oferece
  concluir ou descartar. Sem servidor, o caixa mostra "Sem conexão com o servidor da loja" e segue quando ele volta.
- O servidor transmite NFC-e pendentes a cada 2 minutos e faz o backup diário (ver abaixo).
- Limitação conhecida: o servidor confia no ID do usuário enviado por um caixa conectado (como antes, quando o caixa
  acessava o banco); sessões de usuário assinadas pelo servidor estão nas pendências.

## Regras de domínio já garantidas

- `decimal` e arredondamento único (`Money.Round`: 2 casas, meio para cima), por linha de item.
- Item da venda guarda retrato do produto (descrição, unidade, preço).
- Estoque e caixa são **somente lançamentos**; cancelamento gera movimentos compensatórios.
- Finalização da venda (venda + itens + pagamentos + estoque + caixa + auditoria) em **um único `SaveChanges`** = uma transação.
- **Idempotência:** cada carrinho tem um `ClientRequestId` (índice único). Repetição ou corrida retorna a venda já gravada.
  A UI mantém a mesma chave após falha de comunicação e trava o carrinho até o reenvio.
- Desconto exige usuário com permissão `sales.discount`; cancelamento, `sales.cancel`; sangria/suprimento, `cash.manage`.
- No máximo um caixa aberto por terminal (índice único filtrado no banco).
- Situação fiscal (`FiscalStatus`) separada da situação da venda.
- Auditoria comercial (`AuditEntries`) separada dos logs técnicos.
- Senhas com PBKDF2-SHA256 (210 mil iterações, sal aleatório).

## Como rodar

Pré-requisitos: .NET SDK 10 e PostgreSQL 17 com um usuário `pdv` (senha de desenvolvimento `pdv_dev`) que possa criar
bancos. Duas opções:

- **Instalação local** (usada nesta máquina): instale o PostgreSQL 17 e crie o usuário:

```bash
psql -h localhost -U postgres -c "CREATE ROLE pdv LOGIN CREATEDB PASSWORD 'pdv_dev';"
```

- **Docker**: `docker compose up -d` sobe o contêiner `pdv-postgres` já com esse usuário (`compose.yaml`).

```bash
dotnet test
```

Suba o servidor da loja (em um terminal) e depois o caixa:

```bash
dotnet run --project src/Pdv.Server
```

```bash
dotnet run --project src/Pdv.Desktop
```

O ambiente `Development` (via `Properties/launchSettings.json`) usa o PostgreSQL local, produtos de demonstração e a
SEFAZ simulada. Na primeira execução o servidor cria/migra o banco `sistema_pdv`; o caixa pede o servidor
(`localhost`), conecta-se como terminal `001` e pede a criação do administrador. Os produtos de exemplo usam os
códigos `7891000000011`, `…028`, `…035`, `…042`, `…059`.

Vários caixas no mesmo computador (testes/treinamento): `PDV_DATA_DIR` troca a pasta de dados do caixa e
`Pdv__Terminal__Name` o nome do terminal. No servidor, `Pdv__Server__DataFolder`, `Pdv__Server__Port` e
`Pdv__Server__LocalOnly=true` (escuta só em localhost).

### Atalhos do caixa

| Tecla | Ação |
|---|---|
| `Enter` | Adiciona item (código, `3*código` ou parte da descrição) |
| `F1` | Retaguarda: produtos, compras, relatórios, usuários e dados fiscais (conforme permissão) |
| `F2` / `F12` | Abre / fecha o caixa (fechamento mostra falta ou sobra) |
| `F3` / `F4` | Suprimento / sangria (pede supervisor se o operador não tiver permissão) |
| `F5` | Desconto no item: `2,50` ou `10%` (pede supervisor se necessário) |
| `F6` | Remove o item selecionado |
| `F7` | Cancela uma venda gravada (motivo + supervisor; estorna estoque e dinheiro) |
| `F8` | Reimprime / visualiza o comprovante de uma venda |
| `F9` ou `Enter` no valor | Lança um pagamento; vários lançamentos = pagamento dividido |
| `F10` | Finaliza a venda |
| `F11` | CPF/CNPJ na nota (vazio remove) |
| `Ctrl+Q` | Altera a quantidade do item selecionado |
| `Ctrl+P` | Consulta preço e estoque sem lançar na venda |
| `Ctrl+T` | Devolução / troca de uma venda (parcial; dinheiro ou vale-troca; pede supervisor) |
| `Ctrl+L` | Bloqueia o caixa / troca de operador sem fechar o caixa |
| `Esc` | Descarta a venda em andamento |

### Interface

- Tema em `src/Pdv.Desktop/Themes/Theme.xaml` (cores, botões, campos, tabelas) e ícones vetoriais próprios em
  `Themes/Icons.xaml` (traço em grade 24×24, usados via `IconView`/`IconButton`).
- O logotipo é o mesmo desenho vetorial em todo lugar. Depois de alterá-lo em `Icons.xaml` (`LogoImage`), gere de novo
  o `Assets/app.ico` (executável, janelas e instalador):

```bash
powershell -ExecutionPolicy Bypass -File tools/generate-icon.ps1
```

- No caixa, o último item lido fica em destaque para o cliente acompanhar; todas as teclas de função também
  são botões clicáveis (mouse ou tela de toque). Produtos abaixo do estoque mínimo aparecem em vermelho na retaguarda.

### Cadastro fiscal (Etapa 2)

Na retaguarda (perfil Administrador, permissão `company.manage`):

- **Empresa:** razão social, CNPJ (numérico ou alfanumérico), IE, regime tributário (CRT 1–4), endereço com código
  IBGE e perfil tributário padrão.
- **Tributação:** perfis reutilizáveis com CFOP, CSOSN (Simples/MEI) ou CST do ICMS (regime normal) e PIS/COFINS.
  O sistema impede perfil incompatível com o regime da empresa. Os dois modelos iniciais vêm com alíquotas zeradas:
  revise com o contador.
- **Produto:** NCM, CEST, origem e perfil próprio (ou o padrão da empresa). A aba de produtos avisa quantos itens
  estão sem NCM.

Na venda, cada item grava um retrato fiscal (NCM, CFOP, CSOSN/CST, alíquotas) e a venda grava o CPF/CNPJ do
consumidor — é a base para montar o XML da NFC-e na Etapa 3.

### NFC-e (Etapa 3)

Ativada por computador em **Retaguarda › NFC-e**: ambiente (homologação/produção), série, certificado A1 (.pfx, copiado
para `%ProgramData%\SistemaPDV`) e CSC. A senha do certificado e o CSC ficam cifrados com DPAPI (`nfce.json`).

- A venda é gravada primeiro; a NFC-e é emitida em seguida e **nunca desfaz a venda**. Rejeição avisa o operador e a
  nota pode ser corrigida e reenviada na retaguarda.
- Sem resposta da SEFAZ: a nota é consultada pela chave antes de qualquer reenvio. SEFAZ fora do ar: emissão em
  **contingência offline** (tpEmis 9), DANFE impresso com aviso, transmissão automática quando a SEFAZ voltar.
- Cancelamento da venda (F7) cancela antes a NFC-e na SEFAZ (justificativa ≥ 15 caracteres, dentro do prazo).
- **SEFAZ simulada** (`Pdv:Fiscal:Simulator = true`, ligada no ambiente Development): monta e assina o XML real, mas
  nada é enviado e só funciona em homologação — para desenvolvimento, demonstração e treinamento.

Para homologar de verdade é preciso: certificado A1 da empresa, credenciamento como emissor de NFC-e na SEFAZ da UF
e o CSC de homologação. Biblioteca fiscal: DFe.NET (LGPL 2.1), ver `THIRD-PARTY-NOTICES.md`.

### Operação do dia a dia (Etapa 4)

- **Devolução / troca:** escolhe a venda pelo número e a quantidade de cada item; o valor é proporcional ao que foi
  pago (com desconto). Reembolso em dinheiro (sai da gaveta) ou **vale-troca** (código de 8 caracteres, válido por
  90 dias, aceita uso parcial) — o vale é uma forma de pagamento no caixa. O estoque volta automaticamente.
- **Compras (retaguarda):** fornecedores, entrada manual com vários itens e **importação do XML da NF-e** do
  fornecedor. O sistema aprende o vínculo código do fornecedor → produto e o fator de conversão (ex.: caixa com 12),
  atualiza custo e NCM e recusa nota já lançada.
- **Estoque mínimo** por produto, com alerta na lista de produtos e relatório de reposição.
- **Relatórios:** vendas por dia, formas de pagamento, mais vendidos e margem, caixas, divergências por operador e
  estoque abaixo do mínimo; exportação CSV (Excel) e impressão A4. O fechamento de caixa pode ser impresso na hora
  ou reimpresso pela lista de caixas.

### Comprovante

Comprovante **não fiscal** impresso pelo driver do Windows, em largura de bobina (48 colunas em 80 mm,
32 em 58 mm). Ajustes em `Pdv:Receipt`: `Mode` (`Ask`, `Always`, `Never`), `Printer` (vazio = padrão) e
`PaperWidthMm`. Comandos ESC/POS diretos (guilhotina, abertura de gaveta) ficam para a matriz de hardware.

### Configuração da máquina

Os programas ficam em Arquivos de Programas (somente leitura). Caixa, em `%ProgramData%\SistemaPDV`:

- `connection.json`: endereço do servidor da loja (gravado pelo instalador) e a chave de acesso do caixa, cifrada com
  DPAPI. Se o servidor não responder, o caixa pede outro endereço.
- `appsettings.json` (opcional): demais ajustes, como a impressora (`{ "Pdv": { "Receipt": { "Printer": "..." } } }`).
- `venda-em-andamento.json`: diário da venda em conclusão (some quando o servidor responde).
- `logs\`: logs técnicos diários (30 dias), para o suporte.

Servidor, em `%ProgramData%\SistemaPDV\Servidor` (só Administradores e o serviço):

- `server.json`: conexão com o PostgreSQL e código de conexão de caixas. O instalador grava a senha em texto e o
  servidor a **cifra com DPAPI** ao iniciar; o código também fica cifrado.
- `nfce.json` e `certificado-a1.pfx`: NFC-e da loja (senha e CSC cifrados), configurados pela retaguarda de qualquer caixa.
- `backup.json` e `backups\`: configuração e arquivos de backup (`pdv-AAAAMMDD-HHMMSS.backup`).
- `logs\`: logs do servidor (60 dias).

### Backup

Retaguarda › Sistema: backup automático diário no horário escolhido (se o servidor estava desligado no horário, faz
assim que ligar), retenção em dias (os 3 mais novos nunca são apagados) e **segunda cópia** numa pasta do servidor
(pendrive, rede ou pasta sincronizada com a nuvem — a gravação é testada ao salvar). "Fazer backup agora" e
"Restaurar selecionado" (digitando RESTAURAR): a restauração para os caixas por instantes, guarda antes o estado atual
num backup de segurança, atualiza o esquema se o backup for de versão anterior e mantém os caixas conectados.

Os testes de integração aceitam `PDV_TEST_PG` (ex.: `Host=servidor;Username=pdv;Password=...`). O teste de backup usa
o `pg_dump` local; sem ele, usa o do contêiner `pdv-postgres` (`PDV_TEST_PG_CONTAINER`).

## Instalador

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1 -Version 0.7.0
```

O script publica caixa e servidor **self-contained** para win-x64 (não exigem .NET instalado) em
`artifacts/publish/caixa` e `artifacts/publish/servidor` e compila `installer/SistemaPDV.iss` com o
[Inno Setup 6](https://jrsoftware.org/isdl.php), gerando `artifacts/installer/SistemaPDV-Setup-<versão>.exe`.
Use `-SkipInstaller` para só publicar.

Tipos de instalação:

- **Computador principal:** servidor + caixa. Pede o usuário e a senha do PostgreSQL local, instala o serviço
  `SistemaPDV.Servidor` (início automático, reinício em caso de falha), libera a porta TCP 5080 no firewall (redes
  privada/domínio) e restringe a pasta do servidor a Administradores e SYSTEM. Numa atualização o serviço é parado
  antes de trocar os arquivos.
- **Somente caixa:** pede o nome ou IP do computador principal. Na primeira abertura o caixa pede o código de conexão.

Pré-requisito no computador principal: **PostgreSQL 17** instalado, com o banco/usuário do sistema. Com o servidor
central, o PostgreSQL não precisa mais ser aberto na rede (só o servidor da loja acessa). Caixa e servidor devem ser
atualizados juntos (mesma versão). A instalação silenciosa do PostgreSQL junto com o PDV está na Etapa 7 do roadmap.

### Migrações

```bash
dotnet tool restore
```

```bash
dotnet ef migrations add NomeDaMigracao --project src/Pdv.Infrastructure --output-dir Persistence/Migrations
```

O servidor aplica migrações pendentes ao iniciar (`DatabaseInitializer`).

## Situação do roadmap

**V0.1 Fundação** — feito: camadas, migrações, empresa/loja/terminal iniciais, autenticação e perfis, logs,
tratamento global de erros, backup/restauração com tela e agendamento (Etapa 5), cadastro de usuários e da empresa,
tela de conexão com o servidor, instalador versionado.

**V0.6 Comercialização (Etapa 7)** — feito: licença assinada com plano e limite de caixas, atualização automática dos
caixas, PostgreSQL embutido no instalador, pacote de logs para suporte, gaveta e corte por ESC/POS; falta testar o
instalador em Windows limpo e com hardware real.

**V0.5 Pagamentos integrados (Etapa 6)** — feito: Pix com QR dinâmico (API Pix do Banco Central) e TEF com
confirmação/desfazimento, NSU e bandeira na NFC-e, conciliação de Pix; falta o adaptador TEF da credenciadora e homologar
com o PSP da loja.

**V0.4 Vários caixas (Etapa 5)** — feito: servidor central como serviço do Windows, conexão de caixas por código,
controle de versão, diário da venda contra queda de energia, segredos cifrados com DPAPI, retaguarda › Sistema.

**V0.2 Produtos e estoque** — feito: cadastro e edição de produtos (vários códigos de barras, categoria,
custo, margem), ativação/desativação, entrada de mercadorias, ajuste por contagem com motivo, histórico de
movimentações. Etapa 4: fornecedores na UI, entrada com vários itens, importação do XML da NF-e e estoque mínimo com alerta.

**V0.3 Caixa e vendas** — feito: abertura, venda, itens, desconto por item com liberação de supervisor,
pagamento dividido (dinheiro/cartão/Pix), troco, finalização atômica e idempotente, sangria, suprimento,
fechamento com divergência, cancelamento com estorno e supervisor, comprovante não fiscal com reimpressão,
número sequencial de venda. Etapa 4: devolução parcial com vale-troca e fechamento impresso.

**Concorrência venda × fechamento:** toda operação incrementa a revisão da sessão de caixa (`RowVersion`);
uma venda e um fechamento simultâneos não podem ambos ser gravados — o perdedor recebe uma mensagem e nada
fica parcial (coberto por teste de integração).

**Permissões no backend:** desconto, cancelamento, sangria/suprimento e cadastros são revalidados no banco
pelo ID de quem autorizou; a interface só decide quando pedir o supervisor.
