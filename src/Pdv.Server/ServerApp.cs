using System.Globalization;
using System.Net;
using System.Text.Json;
using Pdv.Application;
using Pdv.Application.Fiscal;
using Pdv.Application.Maintenance;
using Pdv.Application.Payments;
using Pdv.Application.Remote;
using Pdv.Application.Terminals;
using Pdv.Domain.Common;
using Pdv.Fiscal;
using Pdv.Infrastructure;
using Pdv.Infrastructure.Payments;
using Pdv.Infrastructure.Persistence;
using Pdv.Infrastructure.Security;
using Serilog;

namespace Pdv.Server;

/// <summary>
/// Servidor central da loja: único programa com acesso ao banco e ao certificado da NFC-e.
/// Os caixas chamam os serviços da aplicação por HTTP na rede local, identificados pela chave de acesso.
/// </summary>
public static class ServerApp
{
    public static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        configure?.Invoke(builder);

        var folder = builder.Configuration["Pdv:Server:DataFolder"] is { Length: > 0 } f ? f : ServerSettings.DefaultFolder;
        var settings = ServerSettings.Load(folder, builder.Configuration.GetConnectionString("Pdv"));

        builder.Host.UseWindowsService(o => o.ServiceName = "SistemaPDV.Servidor");
        builder.Services.AddSerilog((_, log) => log
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(folder, "logs", "servidor-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 60, formatProvider: CultureInfo.InvariantCulture));

        // "LocalOnly": loja de um caixa só (ou testes) — o servidor não fica exposto na rede.
        var port = builder.Configuration.GetValue("Pdv:Server:Port", RemoteApi.DefaultPort);
        var localOnly = builder.Configuration.GetValue<bool>("Pdv:Server:LocalOnly");
        builder.WebHost.ConfigureKestrel(k => { if (localOnly) k.ListenLocalhost(port); else k.ListenAnyIP(port); });

        builder.Services.AddSingleton(settings);
        builder.Services.AddApplication().AddInfrastructure(settings.ConnectionString);
        if (builder.Configuration.GetValue<bool>("Pdv:Fiscal:Simulator"))
            builder.Services.AddSingleton<INfceGateway>(_ => new SimulatedNfceGateway(new ZeusNfceGateway()));
        else
            builder.Services.AddSingleton<INfceGateway, ZeusNfceGateway>();
        builder.Services.AddSingleton<INfceSettingsStore>(_ => new FileNfceSettingsStore(folder, builder.Configuration["Pdv:Fiscal:SettingsFile"]));
        builder.Services.AddSingleton<IPixSettingsStore>(_ => new FilePixSettingsStore(folder));

        builder.Services.AddSingleton<RpcDispatcher>();
        builder.Services.AddSingleton<TerminalGate>();
        builder.Services.AddSingleton<PairingThrottle>();
        builder.Services.AddSingleton<MaintenanceGate>();
        builder.Services.AddSingleton<BackupRunner>();
        builder.Services.AddScoped<IBackupManager, BackupManager>();
        builder.Services.AddScoped<IServerAdmin, ServerAdmin>();
        builder.Services.AddScoped<IDemoDataSeeder, DemoDataSeeder>();
        builder.Services.AddHostedService<BackupScheduler>();
        builder.Services.AddHostedService<NfceQueueWorker>();
        builder.Services.AddHostedService<PixWatchWorker>();

        var app = builder.Build();
        MapEndpoints(app);
        return app;
    }

    /// <summary>Aplica migrações e dados iniciais. Recusa um banco atualizado por versão mais nova.</summary>
    public static async Task InitializeDatabaseAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(ct);
    }

    private static void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", (MaintenanceGate gate) =>
            Results.Json(new ServerHealth(RemoteApi.Version, Environment.MachineName, gate.Reason is not null), RemoteApi.Json));

        api.MapPost("/terminals/pair", async (HttpContext http, PairRequest request, ServerSettings settings, PairingThrottle throttle,
            TerminalGate terminals, IServiceProvider services, ILogger<PairRequest> log) =>
        {
            if (VersionError(http) is { } versionError) return versionError;
            var remote = http.Connection.RemoteIpAddress;
            // O próprio servidor (caixa instalado no mesmo computador) conecta sem código, a menos que configurado.
            var local = remote is not null && IPAddress.IsLoopback(remote)
                && http.RequestServices.GetRequiredService<IConfiguration>().GetValue("Pdv:Server:LocalPairingWithoutCode", true);
            if (!local)
            {
                if (throttle.IsBlocked(remote))
                    return Error(StatusCodes.Status429TooManyRequests, RemoteError.Unauthorized, "Muitas tentativas com código errado. Aguarde 5 minutos.");
                if (!settings.MatchesPairingCode(request.PairingCode))
                {
                    throttle.Fail(remote);
                    log.LogWarning("Código de conexão incorreto vindo de {Address} ({Machine})", remote, request.MachineName);
                    return Error(StatusCodes.Status401Unauthorized, RemoteError.Unauthorized, "Código de conexão incorreto.");
                }
                throttle.Succeed(remote);
            }
            try
            {
                await using var scope = services.CreateAsyncScope();
                var (terminal, key) = await scope.ServiceProvider.GetRequiredService<TerminalRegistry>().PairAsync(request.MachineName, http.RequestAborted);
                terminals.Forget(); // a chave anterior deste computador deixa de valer na hora
                log.LogInformation("Caixa {Code} ({Machine}) conectado a partir de {Address}", terminal.Code, terminal.MachineName, remote);
                return Results.Json(new PairResponse(terminal, key), RemoteApi.Json);
            }
            catch (DomainException ex)
            {
                return Error(StatusCodes.Status422UnprocessableEntity, RemoteError.Domain, ex.Message);
            }
        });

        api.MapGet("/terminals/me", async (HttpContext http, TerminalGate terminals) =>
        {
            if (VersionError(http) is { } versionError) return versionError;
            var terminal = await AuthenticateAsync(http, terminals);
            return terminal is null ? NotPaired() : Results.Json(terminal, RemoteApi.Json);
        });

        api.MapPost("/rpc/{service}/{method}", async (HttpContext http, string service, string method, TerminalGate terminals,
            RpcDispatcher dispatcher, MaintenanceGate maintenance, ILogger<RpcDispatcher> log) =>
        {
            if (VersionError(http) is { } versionError) return versionError;
            if (!maintenance.TryEnter())
                return Error(StatusCodes.Status503ServiceUnavailable, RemoteError.Maintenance,
                    $"Servidor em manutenção ({maintenance.Reason}). Aguarde alguns minutos e tente de novo.");
            try
            {
                var terminal = await AuthenticateAsync(http, terminals);
                if (terminal is null) return NotPaired();

                var target = dispatcher.Find(service, method);
                using var args = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                var (value, type) = await dispatcher.InvokeAsync(http.RequestServices, target, args.RootElement, terminal, http.RequestAborted);
                return type is null ? Results.NoContent()
                    : value is null ? Results.Content("null", "application/json")
                    : Results.Json(value, RemoteApi.Json.GetTypeInfo(type));
            }
            catch (DomainException ex)
            {
                return Error(StatusCodes.Status422UnprocessableEntity, RemoteError.Domain, ex.Message);
            }
            catch (RpcForbiddenException ex)
            {
                log.LogWarning("Chamada recusada {Service}/{Method}: {Message}", service, method, ex.Message);
                return Error(StatusCodes.Status403Forbidden, RemoteError.Forbidden, ex.Message);
            }
            catch (Exception ex) when (ex is RpcBadRequestException or JsonException)
            {
                return Error(StatusCodes.Status400BadRequest, RemoteError.Server, ex.Message);
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                // O caixa desistiu (queda de rede/energia): o que foi gravado está gravado; a venda é idempotente.
                log.LogWarning("Chamada {Service}/{Method} interrompida pelo caixa", service, method);
                return Results.Empty;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Erro em {Service}/{Method}", service, method);
                return Error(StatusCodes.Status500InternalServerError, RemoteError.Server,
                    "Erro inesperado no servidor. Os detalhes foram gravados no log do servidor.");
            }
            finally
            {
                maintenance.Exit();
            }
        });
    }

    private static Task<TerminalInfo?> AuthenticateAsync(HttpContext http, TerminalGate terminals)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        return terminals.AuthenticateAsync(key, http.Request.Headers[RemoteApi.VersionHeader].ToString(), http.RequestAborted);
    }

    private static IResult? VersionError(HttpContext http)
    {
        var version = http.Request.Headers[RemoteApi.VersionHeader].ToString();
        if (version == RemoteApi.Version) return null;
        return Error(StatusCodes.Status426UpgradeRequired, RemoteError.VersionMismatch,
            $"Este caixa está na versão {(version.Length == 0 ? "?" : version)} e o servidor na {RemoteApi.Version}. " +
            "Instale a mesma versão do Sistema PDV neste computador.");
    }

    private static IResult NotPaired() => Error(StatusCodes.Status401Unauthorized, RemoteError.Unauthorized,
        "Este computador não está conectado ao servidor da loja (ou foi desconectado na retaguarda).");

    private static IResult Error(int status, string kind, string message) =>
        Results.Json(new RemoteError(kind, message), RemoteApi.Json, statusCode: status);
}
