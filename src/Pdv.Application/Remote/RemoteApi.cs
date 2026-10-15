using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Fiscal;
using Pdv.Application.Identity;
using Pdv.Application.Maintenance;
using Pdv.Application.Payments;
using Pdv.Application.Purchases;
using Pdv.Application.Reports;
using Pdv.Application.Sales;
using Pdv.Application.Terminals;

namespace Pdv.Application.Remote;

/// <summary>
/// Contrato entre os caixas e o servidor da loja. Cada chamada é "serviço/método" com os argumentos em JSON;
/// só os serviços desta lista podem ser chamados remotamente.
/// </summary>
public static class RemoteApi
{
    /// <summary>Caixa e servidor precisam estar na mesma versão (major.minor.patch).</summary>
    public static string Version { get; } = typeof(RemoteApi).Assembly.GetName().Version!.ToString(3);

    public const string VersionHeader = "X-Pdv-Version";
    public const int DefaultPort = 5080;

    /// <summary>
    /// Parâmetro com este nome identifica o caixa que faz a chamada: o servidor confere com a chave de acesso,
    /// para um caixa não operar a gaveta de outro.
    /// </summary>
    public const string TerminalParameter = "terminalId";

    public static IReadOnlyList<Type> Services { get; } =
    [
        typeof(AuthenticationService), typeof(UserAdminService), typeof(ProductAdminService), typeof(ProductLookupService),
        typeof(SalesQueryService), typeof(CompanySettingsService), typeof(TaxProfileService), typeof(NfceService),
        typeof(NfceConfigurationService), typeof(SupplierService), typeof(PurchaseService), typeof(ReturnService),
        typeof(ReportService), typeof(CashService), typeof(CheckoutService), typeof(TerminalAdminService),
        typeof(PixService), typeof(PixConfigurationService),
        typeof(IBackupManager), typeof(IServerAdmin), typeof(IDemoDataSeeder),
    ];

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // acentos legíveis no log e nas mensagens
    };
}

/// <summary>Erro devolvido pelo servidor.</summary>
public sealed record RemoteError(string Kind, string Message)
{
    public const string Domain = "domain";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string VersionMismatch = "version";
    public const string Maintenance = "maintenance";
    public const string Server = "server";
}

public sealed record ServerHealth(string Version, string MachineName, bool Maintenance);

public sealed record PairRequest(string MachineName, string? PairingCode);

public sealed record PairResponse(TerminalInfo Terminal, string AccessKey);
