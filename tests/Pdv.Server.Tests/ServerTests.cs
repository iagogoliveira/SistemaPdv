using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pdv.Application;
using Pdv.Application.Cash;
using Pdv.Application.Catalog;
using Pdv.Application.Maintenance;
using Pdv.Application.Remote;
using Pdv.Application.Sales;
using Pdv.Application.Terminals;
using Pdv.Client;
using Pdv.Domain.Common;
using Pdv.Domain.Sales;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Server.Tests;

[Collection(nameof(ServerGroup))]
public class ServerTests(ServerFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FinalizeSaleCommand Sale(TestTerminal t, Guid requestId, decimal quantity = 1) =>
        new(requestId, t.Info.Id, fx.AdminUserId, [new CheckoutItem(fx.ProductId, quantity)], [new CheckoutPayment(PaymentMethod.Cash, 50m)]);

    private static Task<Result<CashSessionSummary>> OpenCashAsync(TestTerminal t, Guid userId) =>
        t.Runner.RunAsync<CashService, Result<CashSessionSummary>>(c => c.OpenAsync(t.Info.Id, userId, 100m));

    private async Task<int> CountSalesAsync(Guid requestId)
    {
        await using var scope = fx.NewScope();
        return await scope.ServiceProvider.GetRequiredService<PdvDbContext>().Sales.CountAsync(s => s.ClientRequestId == requestId, Ct);
    }

    [Fact]
    public async Task Pairing_requires_code_and_every_call_requires_the_terminal_key()
    {
        var anonymous = fx.NewClient();
        await Assert.ThrowsAsync<TerminalNotPairedException>(() => anonymous.GetCurrentTerminalAsync(Ct));
        await Assert.ThrowsAsync<TerminalNotPairedException>(() =>
            new ServerRunner(anonymous).RunAsync<ProductLookupService, ProductLookupItem?>(s => s.FindByBarcodeAsync("7890000000001", default)));

        var wrong = await Assert.ThrowsAsync<TerminalNotPairedException>(() => anonymous.PairAsync("CAIXA-INVASOR", "0000-0000", Ct));
        Assert.Contains("Código de conexão incorreto", wrong.Message);

        var terminal = await fx.PairAsync("CAIXA-PAREADO");
        var me = await terminal.Client.GetCurrentTerminalAsync(Ct);
        Assert.Equal(terminal.Info.Id, me.Id);
        Assert.Matches(@"^\d{3}$", me.Code);

        // Reconectar troca a chave: a antiga deixa de valer.
        var again = await fx.PairAsync("CAIXA-PAREADO");
        Assert.Equal(terminal.Info.Id, again.Info.Id);
        Assert.NotEqual(terminal.AccessKey, again.AccessKey);
        var stale = fx.NewClient();
        stale.AccessKey = terminal.AccessKey;
        await Assert.ThrowsAsync<TerminalNotPairedException>(() => stale.GetCurrentTerminalAsync(Ct));
    }

    [Fact]
    public async Task Terminal_with_another_version_is_refused()
    {
        using var http = fx.RawHttp();
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/terminals/me");
        request.Headers.Add(RemoteApi.VersionHeader, "0.0.1");
        using var response = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.Contains("Instale a mesma versão", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Terminal_cannot_operate_another_terminals_cash_drawer()
    {
        var a = await fx.PairAsync("CAIXA-A");
        var b = await fx.PairAsync("CAIXA-B");

        var ex = await Assert.ThrowsAsync<ServerException>(() =>
            a.Runner.RunAsync<CashService, Result<CashSessionSummary>>(c => c.OpenAsync(b.Info.Id, fx.AdminUserId, 100m)));
        Assert.Contains("outro caixa", ex.Message);

        Assert.True((await OpenCashAsync(b, fx.AdminUserId)).Succeeded);
        var forged = Sale(b, Guid.NewGuid()); // comando com o TerminalId de B enviado por A
        await Assert.ThrowsAsync<ServerException>(() => a.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(forged, default)));
    }

    [Fact]
    public async Task Domain_errors_and_results_travel_back_to_the_terminal()
    {
        var t = await fx.PairAsync("CAIXA-ERROS");
        // Sem caixa aberto: regra de negócio volta como Result com a mensagem.
        var result = await t.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(Sale(t, Guid.NewGuid()), default));
        Assert.False(result.Succeeded);
        Assert.Contains("caixa", result.Error, StringComparison.OrdinalIgnoreCase);

        // Exceção de domínio (permissão) chega como DomainException.
        await Assert.ThrowsAsync<DomainException>(() =>
            t.Runner.RunAsync<TerminalAdminService, IReadOnlyList<TerminalListItem>>(s => s.ListAsync(Guid.NewGuid(), default)));

        var lookup = await t.Runner.RunAsync<ProductLookupService, IReadOnlyList<PriceCheckRow>>(s => s.PriceCheckAsync("7890000000001", default));
        Assert.Equal("Caderno", Assert.Single(lookup).Product.Name);
    }

    [Fact]
    public async Task Sale_resent_after_lost_response_or_crash_is_recorded_once()
    {
        var t = await fx.PairAsync("CAIXA-QUEDA");
        Assert.True((await OpenCashAsync(t, fx.AdminUserId)).Succeeded);
        var journal = new SaleJournal(Path.Combine(fx.Folder, "caixa-queda"));

        // 1) Resposta perdida: o caixa reenvia a mesma chave.
        var command = Sale(t, Guid.NewGuid(), 2);
        journal.Save(command);
        var first = await t.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(command, default));
        var resent = await t.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(command, default));
        Assert.False(first.Value!.AlreadyProcessed);
        Assert.True(resent.Value!.AlreadyProcessed);
        Assert.Equal(first.Value.Number, resent.Value.Number);

        // 2) Caixa "desligou" antes de apagar o diário: na volta, encontra a venda no servidor pela chave.
        var pending = journal.Load()!;
        var found = await t.Runner.RunAsync<SalesQueryService, SaleListItem?>(q => q.FindByRequestAsync(pending.ClientRequestId, default));
        Assert.Equal(first.Value.Number, found!.Number);
        journal.Clear();
        Assert.Null(journal.Load());
        Assert.Equal(1, await CountSalesAsync(command.ClientRequestId));

        // 3) Conexão cortada no meio do envio (o caixa desiste da chamada): a nova tentativa grava uma única venda.
        var interrupted = Sale(t, Guid.NewGuid());
        var (_, method, args) = ServerRunner.Describe(
            (System.Linq.Expressions.Expression<Func<CheckoutService, Task<Result<SaleReceipt>>>>)(c => c.FinalizeAsync(interrupted, default)));
        using (var cut = new CancellationTokenSource(TimeSpan.FromMilliseconds(5)))
        {
            try { await t.Client.InvokeAsync("CheckoutService", method.Name, args, typeof(Result<SaleReceipt>), TimeSpan.FromMinutes(1), cut.Token); }
            catch (OperationCanceledException) { }
        }
        var retry = await t.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(interrupted, default));
        Assert.True(retry.Succeeded);
        Assert.Equal(1, await CountSalesAsync(interrupted.ClientRequestId));
        Assert.Null(await t.Runner.RunAsync<SalesQueryService, SaleListItem?>(q => q.FindByRequestAsync(Guid.NewGuid(), default)));
    }

    [Fact]
    public async Task Three_terminals_selling_at_once_with_retries_never_duplicate_or_lose_sales()
    {
        var terminals = new List<TestTerminal>();
        for (var i = 1; i <= 3; i++)
        {
            var t = await fx.PairAsync($"LOJA-CAIXA-{i}");
            Assert.True((await OpenCashAsync(t, fx.AdminUserId)).Succeeded);
            terminals.Add(t);
        }
        Task<decimal> StockAsync() => terminals[0].Runner.RunAsync<ProductLookupService, decimal>(s => s.GetStockAsync(fx.ProductId, default));
        var before = await StockAsync();

        // Os três caixas vendem ao mesmo tempo; em cada caixa as vendas são sequenciais (como no balcão),
        // e cada venda é enviada duas vezes simultaneamente (operador apertou F10 de novo durante um travamento).
        const int perTerminal = 12;
        var requests = terminals.SelectMany(t => Enumerable.Range(0, perTerminal).Select(_ => (Terminal: t, Command: Sale(t, Guid.NewGuid())))).ToList();
        var perTerminalResults = await Task.WhenAll(terminals.Select(async t =>
        {
            var results = new List<Result<SaleReceipt>>();
            foreach (var r in requests.Where(r => r.Terminal == t))
                results.AddRange(await Task.WhenAll(
                    t.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(r.Command, default)),
                    t.Runner.RunAsync<CheckoutService, Result<SaleReceipt>>(c => c.FinalizeAsync(r.Command, default))));
            return results;
        }));
        var receipts = perTerminalResults.SelectMany(r => r).ToList();

        Assert.All(receipts, r => Assert.True(r.Succeeded, r.Error));
        var numbers = receipts.Select(r => r.Value!.Number).Distinct().ToList();
        Assert.Equal(requests.Count, numbers.Count);
        foreach (var r in requests) Assert.Equal(1, await CountSalesAsync(r.Command.ClientRequestId));
        Assert.Equal(before - requests.Count, await StockAsync());

        foreach (var t in terminals)
        {
            var session = await t.Runner.RunAsync<CashService, CashSessionSummary?>(c => c.GetOpenSessionAsync(t.Info.Id, default));
            Assert.Equal(100m + perTerminal * 10m, session!.ExpectedCash);
        }
    }

    [Fact]
    public void Remote_contract_has_no_overloads_entities_or_tuples()
    {
        var dispatcher = new RpcDispatcher();
        Assert.True(dispatcher.Methods.Count() > 60);
        var problems = new List<string>();
        foreach (var m in dispatcher.Methods)
        {
            foreach (var p in m.Parameters.Where(p => p.ParameterType != typeof(CancellationToken)))
                Check(p.ParameterType, $"{m.Key}({p.Name})", problems, []);
            if (m.ResultType is not null) Check(m.ResultType, $"{m.Key} → retorno", problems, []);
        }
        Assert.Empty(problems);
    }

    private static readonly Type[] Deserializable =
        [typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>), typeof(IEnumerable<>), typeof(IList<>), typeof(ICollection<>),
         typeof(IReadOnlyDictionary<,>), typeof(IDictionary<,>)];

    private static void Check(Type type, string where, List<string> problems, HashSet<Type> seen)
    {
        if (!seen.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(byte[])) return;
        if (type.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true)
        {
            problems.Add($"{where}: tupla não é serializável");
            return;
        }
        if (typeof(Domain.Common.Entity).IsAssignableFrom(type))
        {
            problems.Add($"{where}: entidade {type.Name} exposta");
            return;
        }
        // Interfaces de coleção que o System.Text.Json sabe criar ao ler; outras (ex.: IReadOnlySet) falham só em execução.
        if (type.IsInterface && type.IsGenericType && !Deserializable.Contains(type.GetGenericTypeDefinition()))
            problems.Add($"{where}: {type.Name} não é desserializável");
        foreach (var arg in type.IsGenericType ? type.GetGenericArguments() : []) Check(arg, where, problems, seen);
        if (type.Namespace?.StartsWith("Pdv", StringComparison.Ordinal) == true)
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Check(prop.PropertyType, $"{where}.{prop.Name}", problems, seen);
    }

    [Fact]
    public async Task Runner_serializes_captured_values_like_local_calls()
    {
        var t = await fx.PairAsync("CAIXA-EXPRESSOES");
        var term = "Cader";
        var holder = new { Term = "7890000000001" };
        var byLocal = await t.Runner.RunAsync<ProductLookupService, IReadOnlyList<ProductLookupItem>>(s => s.SearchAsync(term, 5, default));
        var byMember = await t.Runner.RunAsync<ProductLookupService, ProductLookupItem?>(s => s.FindByBarcodeAsync(holder.Term, default));
        var byExpression = await t.Runner.RunAsync<ProductLookupService, ProductLookupItem?>(s => s.FindByBarcodeAsync("789000000000" + 1, default));
        Assert.Single(byLocal);
        Assert.Equal(byMember!.ProductId, byExpression!.ProductId);

        var args = ServerRunner.Describe((System.Linq.Expressions.Expression<Func<ProductLookupService, Task<decimal>>>)(s => s.GetStockAsync(fx.ProductId, default))).Args;
        Assert.Equal(2, args.Count);
        Assert.Null(args[1]); // CancellationToken não viaja
        Assert.Equal(fx.ProductId, args[0]!.GetValue<Guid>());
        _ = JsonNode.Parse(args.ToJsonString());
    }

    [Fact]
    public async Task Server_offline_gives_a_clear_message()
    {
        var offline = new PdvServerClient(PdvServerClient.CreateHttpClient(new Uri("http://127.0.0.1:1/")));
        var ex = await Assert.ThrowsAsync<ServerUnavailableException>(() => offline.GetHealthAsync(Ct));
        Assert.Contains("Sem conexão com o servidor da loja", ex.Message);
        Assert.Equal(new Uri("http://servidor:5080/"), PdvServerClient.ParseAddress("SERVIDOR"));
        Assert.Equal(new Uri("http://192.168.0.10:6000/"), PdvServerClient.ParseAddress(" 192.168.0.10:6000 "));
    }

    [Fact]
    public async Task Revoked_terminal_cannot_call_and_open_cash_blocks_revocation()
    {
        var admin = await fx.PairAsync("CAIXA-ADMIN");
        var target = await fx.PairAsync("CAIXA-ROUBADO");
        Assert.True((await OpenCashAsync(target, fx.AdminUserId)).Succeeded);

        var blocked = await admin.Runner.RunAsync<TerminalAdminService, Result<bool>>(s => s.RevokeAsync(fx.AdminUserId, target.Info.Id, admin.Info.Id, default));
        Assert.Contains("está aberto", blocked.Error);
        Assert.True((await target.Runner.RunAsync<CashService, Result<CashSessionSummary>>(c => c.CloseAsync(target.Info.Id, fx.AdminUserId, 100m, default))).Succeeded);

        var revoked = await admin.Runner.RunAsync<TerminalAdminService, Result<bool>>(s => s.RevokeAsync(fx.AdminUserId, target.Info.Id, admin.Info.Id, default));
        Assert.True(revoked.Succeeded, revoked.Error);
        fx.App.Services.GetRequiredService<TerminalGate>().Forget();
        await Assert.ThrowsAsync<TerminalNotPairedException>(() => target.Client.GetCurrentTerminalAsync(Ct));

        var list = await admin.Runner.RunAsync<TerminalAdminService, IReadOnlyList<TerminalListItem>>(s => s.ListAsync(fx.AdminUserId, default));
        Assert.False(list.Single(t => t.Id == target.Info.Id).IsPaired);
        Assert.NotNull(list.Single(t => t.Id == admin.Info.Id).LastSeenAt);
    }

    [Fact]
    public async Task Database_from_a_newer_version_is_refused()
    {
        await using var scope = fx.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<PdvDbContext>();
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "__EFMigrationsHistory" VALUES ('29991231000000_Futura', '99.0')""", Ct);
        try
        {
            var ex = await Assert.ThrowsAsync<DatabaseNewerThanAppException>(() =>
                scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().EnsureNotNewerAsync(Ct));
            Assert.Contains("versão mais nova", ex.Message);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '29991231000000_Futura'""", Ct);
        }
    }
}

/// <summary>Backup e restauração pelo servidor (precisa do pg_dump/pg_restore do PostgreSQL instalado).</summary>
[Collection(nameof(ServerGroup))]
public class BackupTests(ServerFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Backup_copies_applies_retention_and_restore_keeps_terminals_connected()
    {
        var t = await fx.PairAsync("CAIXA-BACKUP");
        var copy = Path.Combine(fx.Folder, "pendrive");
        var runner = fx.App.Services.GetRequiredService<BackupRunner>();

        // Configuração validada: caminho relativo é recusado; pasta gravável é aceita.
        var invalid = await t.Runner.RunAsync<IBackupManager, Result<BackupStatus>>(b => b.SaveSettingsAsync(fx.AdminUserId, new BackupSettings(true, new TimeOnly(22, 0), 30, "pendrive"), default));
        Assert.False(invalid.Succeeded);
        var saved = await t.Runner.RunAsync<IBackupManager, Result<BackupStatus>>(b => b.SaveSettingsAsync(fx.AdminUserId, new BackupSettings(true, new TimeOnly(22, 0), 30, copy), default));
        Assert.True(saved.Succeeded, saved.Error);

        // Backups antigos: só os mais novos sobrevivem à retenção.
        Directory.CreateDirectory(runner.Folder);
        for (var i = 1; i <= 4; i++)
        {
            var old = Path.Combine(runner.Folder, $"pdv-2020010{i}-000000.backup");
            await File.WriteAllTextAsync(old, "antigo", Ct);
            File.SetCreationTimeUtc(old, new DateTime(2020, 1, i, 0, 0, 0, DateTimeKind.Utc));
        }

        var backup = await t.Runner.RunAsync<IBackupManager, Result<BackupFileInfo>>(b => b.BackupNowAsync(fx.AdminUserId, default));
        Assert.True(backup.Succeeded, backup.Error);
        Assert.True(backup.Value!.CopiedToSecondFolder);
        Assert.True(File.Exists(Path.Combine(copy, backup.Value.Name)));
        var status = await t.Runner.RunAsync<IBackupManager, BackupStatus>(b => b.GetStatusAsync(fx.AdminUserId, default));
        Assert.Equal([backup.Value.Name, "pdv-20200104-000000.backup", "pdv-20200103-000000.backup"], status.Files.Select(f => f.Name));
        Assert.Null(status.LastError);

        // Depois do backup: um caixa novo é conectado e um produto é criado.
        var late = await fx.PairAsync("CAIXA-NOVO-POS-BACKUP");
        Guid productAfter;
        await using (var scope = fx.NewScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<ProductAdminService>().SaveAsync(fx.AdminUserId,
                new ProductEditModel(null, "Produto pós-backup", "UN", null, 1m, 0.5m, ["7891234567895"], null), Ct);
            productAfter = created.Value;
        }

        var refused = await t.Runner.RunAsync<IBackupManager, Result<string>>(b => b.RestoreAsync(fx.AdminUserId, backup.Value.Name, "sim", default));
        Assert.False(refused.Succeeded);
        var traversal = await t.Runner.RunAsync<IBackupManager, Result<string>>(b => b.RestoreAsync(fx.AdminUserId, "..\\server.json", "RESTAURAR", default));
        Assert.False(traversal.Succeeded);

        var restored = await t.Runner.RunAsync<IBackupManager, Result<string>>(b => b.RestoreAsync(fx.AdminUserId, backup.Value.Name, "RESTAURAR", default));
        Assert.True(restored.Succeeded, restored.Error);
        Assert.Contains("antes-de-restaurar", restored.Value);

        // Produto criado depois do backup sumiu; os dois caixas continuam conectados.
        Assert.Null(await late.Runner.RunAsync<ProductLookupService, ProductLookupItem?>(s => s.FindByBarcodeAsync("7891234567895", default)));
        Assert.Equal(late.Info.Id, (await late.Client.GetCurrentTerminalAsync(Ct)).Id);
        Assert.Equal(t.Info.Id, (await t.Client.GetCurrentTerminalAsync(Ct)).Id);
        Assert.NotEqual(Guid.Empty, productAfter);
    }

    [Theory]
    [InlineData("2026-10-14T21:59", "2026-10-13T22:01", false)] // antes do horário, backup de ontem
    [InlineData("2026-10-14T22:00", "2026-10-13T22:01", true)]  // chegou o horário
    [InlineData("2026-10-14T23:00", "2026-10-14T22:00", false)] // já feito hoje
    [InlineData("2026-10-14T09:00", "2026-10-13T06:00", true)]  // servidor ficou desligado às 22h
    public void Daily_schedule_also_catches_up_missed_backups(string now, string last, bool due)
    {
        static DateTimeOffset At(string s) => new(DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(-3));
        Assert.Equal(due, BackupScheduler.IsDue(new BackupSettings(true, new TimeOnly(22, 0), 30, null), At(last), At(now)));
        Assert.True(BackupScheduler.IsDue(BackupSettings.Default, null, At(now)));
        Assert.False(BackupScheduler.IsDue(BackupSettings.Default with { Enabled = false }, null, At(now)));
    }
}

public class ServerSettingsTests
{
    [Fact]
    public void Plain_password_and_pairing_code_are_protected_in_the_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdv-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "server.json"),
                """{ "ConnectionStrings": { "Pdv": "Host=localhost;Database=sistema_pdv;Username=pdv;Password=Senha-Secreta-123" } }""");

            var settings = ServerSettings.Load(folder, null);
            Assert.Contains("Senha-Secreta-123", settings.ConnectionString);
            Assert.Matches(@"^\d{8}$", settings.PairingCode);

            var file = File.ReadAllText(Path.Combine(folder, "server.json"));
            Assert.DoesNotContain("Senha-Secreta-123", file);
            Assert.DoesNotContain(settings.PairingCode, file);
            Assert.Contains("dpapi:", file);

            var reloaded = ServerSettings.Load(folder, null);
            Assert.Equal(settings.ConnectionString, reloaded.ConnectionString);
            Assert.Equal(settings.PairingCode, reloaded.PairingCode);
            Assert.True(reloaded.MatchesPairingCode(ServerSettings.Format(settings.PairingCode)));
            Assert.False(reloaded.MatchesPairingCode("1"));

            var renewed = reloaded.RegeneratePairingCode();
            Assert.Equal(renewed, ServerSettings.Load(folder, null).PairingCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Terminal_connection_file_keeps_the_key_encrypted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pdv-term-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new TerminalConnectionStore(folder);
            Assert.Equal((null, null), store.Load());
            var key = TerminalAccessKey.New();
            store.Save("servidor", key);
            Assert.Equal(("servidor", key), store.Load());
            Assert.DoesNotContain(key, File.ReadAllText(store.FilePath));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
