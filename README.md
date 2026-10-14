# Sistema PDV

Ponto de venda para Windows em **.NET 10 (LTS)**, com WPF, SQL Server e EF Core 10.
Este repositório implementa o início do MVP descrito no planejamento do produto (V0.1 → V0.3).

## Stack

| Camada | Tecnologia |
|---|---|
| Runtime / linguagem | .NET 10 LTS, C# 14 |
| Interface | WPF + CommunityToolkit.Mvvm 8.4 |
| Hospedagem / DI / config | Microsoft.Extensions.Hosting 10 (`appsettings.json`) |
| Banco | SQL Server Express (dev: LocalDB) |
| Acesso a dados | Entity Framework Core 10 + migrações |
| Logs técnicos | Serilog (arquivo diário em `%ProgramData%\SistemaPDV\logs`) |
| Testes | xUnit v3 no Microsoft.Testing.Platform (`dotnet test`) |
| Solução | formato `.slnx` |

## Estrutura

```
src/
  Pdv.Domain           Entidades e regras de negócio puras (sem dependências)
  Pdv.Application      Casos de uso: login, caixa, finalização de venda, busca de produtos
  Pdv.Infrastructure   EF Core/SQL Server, migrações, hash de senha, backup/restauração
  Pdv.Desktop          App WPF (frente de caixa)
tests/
  Pdv.Domain.Tests          Totais, arredondamento, descontos, troco, caixa, estoque
  Pdv.Infrastructure.Tests  Banco real (LocalDB): idempotência, concorrência, atomicidade, backup
```

Dependências: `Desktop → Application + Infrastructure`, `Infrastructure → Application → Domain`.
Quando o serviço central local (multi-caixa, V0.4) for criado, ele substitui o acesso direto ao banco
pelo `Desktop` sem mudar Domain/Application.

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

Pré-requisitos: .NET SDK 10 e SQL Server LocalDB (vem com o Visual Studio) ou SQL Server Express.

```bash
dotnet test
```

```bash
dotnet run --project src/Pdv.Desktop
```

`dotnet run` usa o ambiente `Development` (LocalDB e produtos de demonstração, via
`Properties/launchSettings.json`). Na primeira execução o banco `SistemaPdv` é criado/migrado, o computador
é registrado como terminal `001` e a tela pede a criação do administrador. Os produtos de exemplo usam os
códigos `7891000000011`, `…028`, `…035`, `…042`, `…059`.

### Atalhos do caixa

| Tecla | Ação |
|---|---|
| `Enter` | Adiciona item (código, `3*código` ou parte da descrição) |
| `F1` | Retaguarda: produtos, estoque, usuários (conforme permissão) |
| `F2` / `F12` | Abre / fecha o caixa (fechamento mostra falta ou sobra) |
| `F3` / `F4` | Suprimento / sangria (pede supervisor se o operador não tiver permissão) |
| `F5` | Desconto no item: `2,50` ou `10%` (pede supervisor se necessário) |
| `F6` | Remove o item selecionado |
| `F7` | Cancela uma venda gravada (motivo + supervisor; estorna estoque e dinheiro) |
| `F8` | Reimprime / visualiza o comprovante de uma venda |
| `F9` ou `Enter` no valor | Lança um pagamento; vários lançamentos = pagamento dividido |
| `F10` | Finaliza a venda |
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
  são botões clicáveis (mouse ou tela de toque). Produtos com estoque até 5 aparecem em vermelho na retaguarda.

### Comprovante

Comprovante **não fiscal** impresso pelo driver do Windows, em largura de bobina (48 colunas em 80 mm,
32 em 58 mm). Ajustes em `Pdv:Receipt`: `Mode` (`Ask`, `Always`, `Never`), `Printer` (vazio = padrão) e
`PaperWidthMm`. Comandos ESC/POS diretos (guilhotina, abertura de gaveta) ficam para a matriz de hardware.

### Configuração da máquina

O programa fica em Arquivos de Programas (somente leitura); cada máquina tem seus ajustes em
`%ProgramData%\SistemaPDV`:

- `connection.json`: servidor SQL. Gravado pelo instalador; se o banco não responder ao abrir o sistema,
  a tela de conexão pede o servidor e grava aqui.
- `appsettings.json` (opcional): demais ajustes, como a impressora (`{ "Pdv": { "Receipt": { "Printer": "..." } } }`).
- `logs\`: logs técnicos diários (30 dias), para o suporte.

Os testes de integração aceitam a variável `PDV_TEST_SQL` (ex.: `.\SQLEXPRESS`).

## Instalador

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1 -Version 0.2.0
```

O script publica o app **self-contained** para win-x64 (não exige .NET instalado no cliente) em
`artifacts/publish` e compila `installer/SistemaPDV.iss` com o [Inno Setup 6](https://jrsoftware.org/isdl.php),
gerando `artifacts/installer/SistemaPDV-Setup-<versão>.exe`. Use `-SkipInstaller` para só publicar.

O instalador pede o servidor SQL (`.\SQLEXPRESS` no computador principal; `SERVIDOR\SQLEXPRESS` nos demais
caixas), cria `%ProgramData%\SistemaPDV` com permissão de escrita para os usuários e mantém configuração
e logs ao desinstalar.

Pré-requisito no computador principal: **SQL Server Express** instalado. Para caixas em rede: habilitar TCP/IP
e liberar a porta no firewall, e usar autenticação Windows com contas reconhecidas pelo servidor (ou editar
`connection.json` com um login SQL). Assistente para isso fica para a etapa de multi-caixa.

### Migrações

```bash
dotnet tool restore
```

```bash
dotnet ef migrations add NomeDaMigracao --project src/Pdv.Infrastructure --output-dir Persistence/Migrations
```

O app aplica migrações pendentes ao iniciar (`DatabaseInitializer`).

## Situação do roadmap

**V0.1 Fundação** — feito: camadas, migrações, empresa/loja/terminal iniciais, autenticação e perfis, logs,
tratamento global de erros, backup/restauração (testado), cadastro de usuários, tela de conexão com o
servidor, instalador versionado. Falta: tela de backup/restauração, cadastro dos dados da empresa na UI.

**V0.2 Produtos e estoque** — feito: cadastro e edição de produtos (vários códigos de barras, categoria,
custo, margem), ativação/desativação, entrada de mercadorias, ajuste por contagem com motivo, histórico de
movimentações. Falta: cadastro de fornecedores na UI, entrada por compra com vários itens, alerta de estoque baixo.

**V0.3 Caixa e vendas** — feito: abertura, venda, itens, desconto por item com liberação de supervisor,
pagamento dividido (dinheiro/cartão/Pix), troco, finalização atômica e idempotente, sangria, suprimento,
fechamento com divergência, cancelamento com estorno e supervisor, comprovante não fiscal com reimpressão,
número sequencial de venda. Falta: devolução parcial, relatório de fechamento impresso.

**Concorrência venda × fechamento:** toda operação incrementa a revisão da sessão de caixa (`RowVersion`);
uma venda e um fechamento simultâneos não podem ambos ser gravados — o perdedor recebe uma mensagem e nada
fica parcial (coberto por teste de integração).

**Permissões no backend:** desconto, cancelamento, sangria/suprimento e cadastros são revalidados no banco
pelo ID de quem autorizou; a interface só decide quando pedir o supervisor.
