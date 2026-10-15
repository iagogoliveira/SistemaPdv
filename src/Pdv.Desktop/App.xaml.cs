using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pdv.Application.Maintenance;
using Pdv.Application.Remote;
using Pdv.Application.Terminals;
using Pdv.Client;
using Pdv.Desktop.Services;
using Pdv.Desktop.ViewModels;
using Pdv.Desktop.Views;
using Pdv.Domain.Common;
using Serilog;

namespace Pdv.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ConfigureLogging();
        ConfigureCulture();

        DispatcherUnhandledException += (_, args) =>
        {
            // Falhas de comunicação e regras de negócio já vêm com mensagem pronta para o operador.
            var friendly = args.Exception is ServerException or DomainException;
            if (friendly) Log.Warning("{Message}", args.Exception.Message);
            else Log.Error(args.Exception, "Erro não tratado na interface");
            ShowMessage(friendly ? args.Exception.Message
                : $"Ocorreu um erro inesperado:\n{args.Exception.Message}\n\nDetalhes foram gravados no log.");
            args.Handled = true;
        };

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddJsonFile(LocalSettings.SettingsFilePath, optional: true)
                .AddEnvironmentVariables()
                .Build();
            var machineName = configuration["Pdv:Terminal:Name"] is { Length: > 0 } name ? name : Environment.MachineName;

            var connection = await ConnectAsync(new TerminalConnectionStore(LocalSettings.Folder), machineName);
            if (connection is null)
            {
                Shutdown(1);
                return;
            }

            _host = BuildHost(e.Args, connection.Value.Client);
            await _host.StartAsync();
            var services = _host.Services;
            var state = services.GetRequiredService<AppState>();
            state.Terminal = connection.Value.Terminal;
            Log.Information("PDV {Version} iniciado no terminal {Terminal} ({Machine}), servidor {Server}",
                RemoteApi.Version, state.Terminal.Code, state.Terminal.MachineName, connection.Value.Client.BaseAddress);

            var login = new LoginWindow(services.GetRequiredService<LoginViewModel>());
            if (login.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            // O servidor só cria produtos de demonstração quando configurado para isso.
            await services.GetRequiredService<ServerRunner>().RunAsync<IDemoDataSeeder>(s => s.SeedAsync(state.User!.UserId, default));

            var main = new MainWindow(services.GetRequiredService<CheckoutViewModel>());
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha ao iniciar");
            ShowMessage($"Não foi possível iniciar o sistema:\n{ex.Message}");
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    /// <summary>
    /// Encontra o servidor da loja, confere a versão e garante que este computador está conectado (chave de acesso).
    /// Na primeira vez pede o endereço do servidor e o código de conexão mostrado na retaguarda.
    /// </summary>
    private static async Task<(PdvServerClient Client, TerminalInfo Terminal)?> ConnectAsync(TerminalConnectionStore store, string machineName)
    {
        var (address, key) = store.Load();
        string? problem = null;
        while (true)
        {
            if (address is null || problem is not null)
            {
                address = Prompt("Servidor da loja",
                    (problem is null ? "" : problem + "\n\n") +
                    "Informe o computador onde está o servidor do Sistema PDV (ex.: SERVIDOR ou 192.168.0.10; localhost neste computador):",
                    address ?? "localhost");
                if (string.IsNullOrWhiteSpace(address)) return null;
                problem = null;
            }

            PdvServerClient client;
            try
            {
                client = new PdvServerClient(PdvServerClient.CreateHttpClient(PdvServerClient.ParseAddress(address))) { AccessKey = key };
            }
            catch (UriFormatException)
            {
                problem = $"Endereço inválido: {address}";
                continue;
            }

            try
            {
                var health = await client.GetHealthAsync();
                if (health.Version != RemoteApi.Version)
                {
                    ShowMessage($"O servidor da loja ({health.MachineName}) está na versão {health.Version} e este caixa na {RemoteApi.Version}.\n\n" +
                        "Instale neste computador a mesma versão do servidor.");
                    return null;
                }

                if (key is not null)
                {
                    try
                    {
                        var terminal = await client.GetCurrentTerminalAsync();
                        store.Save(address, key);
                        return (client, terminal);
                    }
                    catch (TerminalNotPairedException ex)
                    {
                        Log.Warning("Chave do caixa recusada pelo servidor: {Message}", ex.Message);
                        key = client.AccessKey = null;
                    }
                }

                var pair = await PairAsync(client, address, machineName);
                if (pair is null) return null;
                store.Save(address, pair.AccessKey);
                client.AccessKey = pair.AccessKey;
                Log.Information("Caixa conectado ao servidor {Server} como {Code}", address, pair.Terminal.Code);
                return (client, pair.Terminal);
            }
            catch (ServerUnavailableException ex)
            {
                Log.Warning("Servidor {Address} indisponível: {Message}", address, ex.Message);
                problem = ex.Message;
            }
            catch (VersionMismatchException ex)
            {
                ShowMessage(ex.Message);
                return null;
            }
        }
    }

    /// <summary>No próprio computador do servidor a conexão é direta; nos demais pede o código de conexão.</summary>
    private static async Task<PairResponse?> PairAsync(PdvServerClient client, string address, string machineName)
    {
        string? code = null;
        var tryWithoutCode = IsLocal(address);
        while (true)
        {
            if (!tryWithoutCode)
            {
                code = Prompt("Conectar este caixa",
                    $"Este computador ({machineName}) ainda não está conectado ao servidor da loja.\n\n" +
                    "Informe o código de conexão, mostrado no servidor em Retaguarda › Sistema:", "");
                if (code is null) return null;
            }
            try
            {
                return await client.PairAsync(machineName, code);
            }
            catch (TerminalNotPairedException ex)
            {
                if (!tryWithoutCode) ShowMessage(ex.Message);
                tryWithoutCode = false;
            }
            catch (DomainException ex)
            {
                ShowMessage(ex.Message);
                return null;
            }
        }
    }

    private static bool IsLocal(string address)
    {
        var host = PdvServerClient.ParseAddress(address).Host;
        return host is "localhost" or "127.0.0.1" or "[::1]" || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Prompt(string title, string message, string initial)
    {
        var dialog = new InputDialog(title, message, initial);
        return dialog.ShowDialog() == true ? dialog.Value.Trim() : null;
    }

    private static void ShowMessage(string message) => new MessageDialog(message, isError: true).ShowDialog();

    private static IHost BuildHost(string[] args, PdvServerClient client)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddJsonFile(LocalSettings.SettingsFilePath, optional: true, reloadOnChange: false);

        builder.Services.AddSerilog();
        builder.Services.AddSingleton(client);
        builder.Services.AddSingleton<ServerRunner>();
        builder.Services.AddSingleton(new SaleJournal(LocalSettings.Folder));
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddSingleton<ReceiptPrinter>();
        builder.Services.AddSingleton<TicketPrinter>();
        builder.Services.AddSingleton<Func<LoginViewModel>>(sp => sp.GetRequiredService<LoginViewModel>);
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<SupervisorAuthorizer>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<CheckoutViewModel>();
        builder.Services.AddTransient<AdminViewModel>();
        builder.Services.AddSingleton<Func<AdminViewModel>>(sp => sp.GetRequiredService<AdminViewModel>);
        return builder.Build();
    }

    private static void ConfigureCulture()
    {
        var culture = new CultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    private static void ConfigureLogging()
    {
        // Logs técnicos ficam fora da pasta do programa para que o suporte possa exportá-los.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(LocalSettings.Folder, "logs", "pdv-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30, formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }
}
