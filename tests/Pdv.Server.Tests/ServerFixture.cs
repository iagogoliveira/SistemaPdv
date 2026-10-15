using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application;
using Pdv.Application.Identity;
using Pdv.Application.Terminals;
using Pdv.Client;
using Pdv.Domain.Catalog;
using Pdv.Domain.Inventory;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Server.Tests;

/// <summary>
/// Servidor completo em memória (TestServer) sobre um PostgreSQL real, com banco e pasta de dados próprios.
/// Os caixas usam o mesmo cliente HTTP do programa do caixa.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "pdv-srv-" + Guid.NewGuid().ToString("N")[..8]);

    public string ConnectionString { get; } =
        $"{Environment.GetEnvironmentVariable("PDV_TEST_PG") ?? "Host=localhost;Username=pdv;Password=pdv_dev"};Database=pdv_srv_{Guid.NewGuid():N}";

    public WebApplication App { get; private set; } = null!;
    public Guid AdminUserId { get; private set; }
    public Guid ProductId { get; private set; }
    public const decimal InitialStock = 10_000;

    public string PairingCode => App.Services.GetRequiredService<ServerSettings>().PairingCode;

    private TestServer Server => (TestServer)App.Services.GetRequiredService<IServer>();

    public const string MachineCode = "TEST-0000-0000-0001";

    /// <summary>Par de chaves só dos testes: a licença do servidor de teste é emitida aqui (50 caixas).</summary>
    public (string PrivatePem, string PublicPem) LicenseKeys { get; } = Application.Licensing.LicenseCodec.NewKeyPair();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllTextAsync(Path.Combine(Folder, "chave-publica.pem"), LicenseKeys.PublicPem);
        await File.WriteAllTextAsync(Path.Combine(Folder, "licenca.pdvlic"), IssueLicense(50, null));
        App = ServerApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Pdv"] = ConnectionString,
                ["Pdv:Server:DataFolder"] = Folder,
                ["Pdv:Fiscal:SettingsFile"] = Path.Combine(Folder, "nfce-desligada.json"),
                ["Pdv:License:PublicKeyFile"] = Path.Combine(Folder, "chave-publica.pem"),
                ["Pdv:License:MachineCode"] = MachineCode,
            });
        });
        await ServerApp.InitializeDatabaseAsync(App.Services);
        await App.StartAsync();

        await using var scope = App.Services.CreateAsyncScope();
        AdminUserId = (await scope.ServiceProvider.GetRequiredService<AuthenticationService>()
            .CreateInitialAdminAsync("admin", "Admin", "senha123")).Value!.UserId;
        var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
        var product = new Product("Caderno", 10m, 6m);
        product.AddBarcode("7890000000001");
        db.Products.Add(product);
        db.StockMovements.Add(StockMovement.ForPurchase(product.Id, InitialStock, null, AdminUserId, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        ProductId = product.Id;
    }

    /// <summary>Um "computador de caixa": cliente HTTP próprio, conectado com o código.</summary>
    public async Task<TestTerminal> PairAsync(string machineName)
    {
        var client = NewClient();
        var pair = await client.PairAsync(machineName, PairingCode);
        client.AccessKey = pair.AccessKey;
        return new TestTerminal(client, new ServerRunner(client), pair.Terminal, pair.AccessKey);
    }

    public string IssueLicense(int terminals, DateOnly? expires, string machine = MachineCode) =>
        Application.Licensing.LicenseCodec.Sign(new Application.Licensing.LicenseData("LIC-TESTE", "Loja de Teste", null, "Teste", terminals,
            DateOnly.FromDateTime(DateTime.Today), expires, machine), LicenseKeys.PrivatePem);

    public PdvServerClient NewClient() => new(Server.CreateClient());

    public HttpClient RawHttp() => Server.CreateClient();

    public AsyncServiceScope NewScope() => App.Services.CreateAsyncScope();

    public async ValueTask DisposeAsync()
    {
        await using (var scope = App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PdvDbContext>().Database.EnsureDeletedAsync();
        await App.StopAsync();
        await App.DisposeAsync();
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { } // log ainda aberto: a pasta temporária fica para trás
    }
}

public sealed record TestTerminal(PdvServerClient Client, ServerRunner Runner, TerminalInfo Info, string AccessKey);

[CollectionDefinition(nameof(ServerGroup))]
public sealed class ServerGroup : ICollectionFixture<ServerFixture>;
