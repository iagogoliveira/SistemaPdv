using Pdv.Infrastructure.Persistence;
using Pdv.Server;
using Serilog;

// "SistemaPDV.Servidor.exe --codigo" mostra o código para conectar novos caixas (executar no próprio servidor).
if (args.Contains("--codigo"))
{
    var folder = Environment.GetEnvironmentVariable("Pdv__Server__DataFolder") is { Length: > 0 } f ? f : ServerSettings.DefaultFolder;
    var settings = ServerSettings.Load(folder, "Host=localhost");
    Console.WriteLine($"Código de conexão de caixas: {ServerSettings.Format(settings.PairingCode)}");
    return 0;
}

var app = ServerApp.Build(args);
try
{
    await ServerApp.InitializeDatabaseAsync(app.Services);
}
catch (DatabaseNewerThanAppException ex)
{
    app.Logger.LogCritical("{Message}", ex.Message);
    await Log.CloseAndFlushAsync();
    return 2;
}

app.Logger.LogInformation("Servidor do Sistema PDV {Version} iniciado em {Machine}", Pdv.Application.Remote.RemoteApi.Version, Environment.MachineName);
await app.RunAsync();
return 0;
