using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pdv.Application;
using Pdv.Desktop.Services;
using Pdv.Desktop.ViewModels;
using Pdv.Desktop.Views;
using Pdv.Infrastructure;
using Pdv.Infrastructure.Persistence;
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
            Log.Error(args.Exception, "Erro não tratado na interface");
            MessageBox.Show($"Ocorreu um erro inesperado:\n{args.Exception.Message}\n\nDetalhes foram gravados no log.",
                "Sistema PDV", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            _host = BuildHost(e.Args);
            await _host.StartAsync();

            var services = _host.Services;
            var state = services.GetRequiredService<AppState>();
            var runner = services.GetRequiredService<ScopedRunner>();

            try
            {
                state.Terminal = await runner.RunAsync<DatabaseInitializer, Domain.Stores.Terminal>(async init =>
                {
                    await init.InitializeAsync();
                    return await init.EnsureTerminalAsync(Environment.MachineName);
                });
            }
            catch (Exception ex) when (IsServerUnreachable(ex))
            {
                // Só oferece trocar o servidor quando ele não respondeu. Outros erros de banco
                // (senha, permissão, migração) seguem para a mensagem de erro real abaixo.
                Log.Error(ex, "Falha ao conectar ao PostgreSQL");
                if (AskForServer(services.GetRequiredService<IConfiguration>().GetConnectionString("Pdv"), ex)) Restart();
                else Shutdown(1);
                return;
            }
            Log.Information("PDV {Version} iniciado no terminal {Terminal} ({Machine})",
                typeof(App).Assembly.GetName().Version, state.Terminal.Code, Environment.MachineName);

            var login = new LoginWindow(services.GetRequiredService<LoginViewModel>());
            if (login.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            if (services.GetRequiredService<IConfiguration>().GetValue<bool>("Pdv:SeedDemoData"))
                await runner.RunAsync<DatabaseInitializer>(init => init.SeedDemoDataAsync(state.User!.UserId));

            var main = new MainWindow(services.GetRequiredService<CheckoutViewModel>());
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha ao iniciar");
            MessageBox.Show($"Não foi possível iniciar o sistema:\n{ex.Message}", "Sistema PDV",
                MessageBoxButton.OK, MessageBoxImage.Error);
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

    /// <summary>Sem conexão com o banco: permite informar o servidor da loja e grava na configuração da máquina.</summary>
    private static bool AskForServer(string? currentConnectionString, Exception error)
    {
        var dialog = new InputDialog("Conexão com o banco de dados",
            $"O servidor do banco de dados não respondeu.\n{Innermost(error).Message}\n\n" +
            "Informe o computador onde está o PostgreSQL (ex.: SERVIDOR ou 192.168.0.10; localhost neste computador):",
            LocalSettings.CurrentServer(currentConnectionString) ?? "localhost");
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return false;

        LocalSettings.SaveConnectionString(LocalSettings.WithServer(currentConnectionString, dialog.Value));
        Log.Information("Servidor do banco alterado para {Server}", dialog.Value);
        return true;
    }

    /// <summary>Servidor desligado, nome errado, porta bloqueada ou sem resposta (não inclui senha incorreta).</summary>
    private static bool IsServerUnreachable(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SocketException or TimeoutException) return true;
        return false;
    }

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    private void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        Shutdown();
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration
            .AddJsonFile(LocalSettings.SettingsFilePath, optional: true, reloadOnChange: false)
            .AddJsonFile(LocalSettings.ConnectionFilePath, optional: true, reloadOnChange: false);

        var connectionString = builder.Configuration.GetConnectionString("Pdv")
            ?? throw new InvalidOperationException("ConnectionStrings:Pdv não configurada.");

        builder.Services.AddSerilog();
        builder.Services.AddApplication().AddInfrastructure(connectionString);
        builder.Services.AddSingleton<AppState>();
        builder.Services.AddSingleton<ScopedRunner>();
        builder.Services.AddSingleton<ReceiptPrinter>();
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
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(LocalSettings.Folder, "logs", "pdv-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30, formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }
}
