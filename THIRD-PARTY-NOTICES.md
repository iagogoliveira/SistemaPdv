# Componentes de terceiros

O Sistema PDV usa as bibliotecas abaixo, sem modificações, por meio de pacotes NuGet. As DLLs são distribuídas
separadas do executável do sistema e podem ser substituídas por outras versões compatíveis.

| Componente | Uso | Licença |
|---|---|---|
| [DFe.NET / Zeus.Net.NFe.NFCe](https://github.com/ZeusAutomacao/DFe.NET) | Montagem, assinatura e transmissão da NFC-e | GNU LGPL 2.1 |
| [Npgsql / Npgsql.EntityFrameworkCore.PostgreSQL](https://www.npgsql.org) | Acesso ao PostgreSQL | PostgreSQL License |
| [Entity Framework Core](https://github.com/dotnet/efcore) | Acesso a dados | MIT |
| [.NET / WPF / Microsoft.Extensions.*](https://github.com/dotnet) | Plataforma | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM | MIT |
| [Serilog](https://serilog.net) | Logs técnicos | Apache 2.0 |
| [QRCoder](https://github.com/codebude/QRCoder) | QR Code do DANFE NFC-e | MIT |

## GNU LGPL 2.1 (DFe.NET)

A DFe.NET é software livre sob a GNU Lesser General Public License, versão 2.1. O texto completo da licença está em
<https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html> e o código-fonte da biblioteca em
<https://github.com/ZeusAutomacao/DFe.NET>. Este produto apenas referencia as DLLs publicadas
(`NFe.*.dll`, `DFe.*.dll`); o usuário pode substituí-las por versões modificadas da biblioteca.
