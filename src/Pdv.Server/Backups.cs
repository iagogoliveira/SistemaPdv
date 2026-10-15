using System.Text.Json;
using System.Text.RegularExpressions;
using Pdv.Application;
using Pdv.Application.Identity;
using Pdv.Application.Maintenance;
using Pdv.Application.Terminals;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Identity;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Server;

/// <summary>
/// Executa os backups (pg_dump) na pasta do servidor, copia para a segunda pasta (pendrive, rede ou pasta
/// sincronizada com a nuvem) e aplica a retenção. Um backup por vez.
/// </summary>
public sealed partial class BackupRunner(ServerSettings server, BackupService backups, TimeProvider clock, ILogger<BackupRunner> log) : IDisposable
{
    /// <summary>Mesmo com retenção curta, os backups mais recentes nunca são apagados.</summary>
    private const int KeepAtLeast = 3;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Folder => Path.Combine(server.Folder, "backups");

    public void Dispose() => _lock.Dispose();
    private string SettingsPath => Path.Combine(server.Folder, "backup.json");

    [GeneratedRegex(@"^pdv-\d{8}-\d{6}(-[a-z\-]+)?\.backup$")]
    private static partial Regex BackupName();

    public BackupSettings LoadSettings() => LoadState().Settings ?? BackupSettings.Default;

    public void SaveSettings(BackupSettings settings)
    {
        var state = LoadState();
        SaveState(state with { Settings = settings });
    }

    public (DateTimeOffset? LastBackupAt, string? LastError) LastRun()
    {
        var state = LoadState();
        return (state.LastBackupAt, state.LastError);
    }

    public IReadOnlyList<BackupFileInfo> List()
    {
        var copy = LoadSettings().CopyFolder;
        if (!Directory.Exists(Folder)) return [];
        return new DirectoryInfo(Folder).GetFiles("pdv-*.backup")
            .Where(f => BackupName().IsMatch(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new BackupFileInfo(f.Name, new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero), f.Length,
                copy is not null && File.Exists(Path.Combine(copy, f.Name))))
            .ToList();
    }

    /// <summary>Faz o backup agora. Falha na cópia para a segunda pasta não invalida o backup local, mas fica registrada.</summary>
    public async Task<BackupFileInfo> RunAsync(string? suffix, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Folder);
            var now = clock.GetLocalNow();
            var name = $"pdv-{now:yyyyMMdd-HHmmss}{(suffix is null ? "" : "-" + suffix)}.backup";
            var path = Path.Combine(Folder, name);
            var temp = path + ".partial";
            try
            {
                await backups.BackupAsync(temp, ct);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                if (File.Exists(temp)) File.Delete(temp);
                SaveState(LoadState() with { LastError = $"{now:dd/MM HH:mm} backup falhou: {ex.Message}" });
                log.LogError(ex, "Backup falhou");
                throw;
            }

            var settings = LoadSettings();
            string? error = null;
            var copied = false;
            if (!string.IsNullOrWhiteSpace(settings.CopyFolder))
            {
                try
                {
                    Directory.CreateDirectory(settings.CopyFolder);
                    File.Copy(path, Path.Combine(settings.CopyFolder, name), overwrite: true);
                    copied = true;
                    ApplyRetention(settings.CopyFolder, settings.RetentionDays);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    error = $"{now:dd/MM HH:mm} backup feito, mas a cópia para \"{settings.CopyFolder}\" falhou: {ex.Message}";
                    log.LogWarning(ex, "Cópia do backup para {Folder} falhou", settings.CopyFolder);
                }
            }
            ApplyRetention(Folder, settings.RetentionDays);
            SaveState(LoadState() with { LastBackupAt = clock.GetUtcNow(), LastError = error });
            log.LogInformation("Backup {Name} concluído ({Size:N0} bytes, cópia: {Copied})", name, new FileInfo(path).Length, copied);
            return new BackupFileInfo(name, clock.GetUtcNow(), new FileInfo(path).Length, copied);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Caminho do backup pelo nome, procurando na pasta do servidor e depois na segunda pasta.</summary>
    public string? Resolve(string fileName)
    {
        if (!BackupName().IsMatch(fileName)) return null;
        var local = Path.Combine(Folder, fileName);
        if (File.Exists(local)) return local;
        var copy = LoadSettings().CopyFolder;
        return copy is not null && File.Exists(Path.Combine(copy, fileName)) ? Path.Combine(copy, fileName) : null;
    }

    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { return await action(); }
        finally { _lock.Release(); }
    }

    private void ApplyRetention(string folder, int days)
    {
        var limit = clock.GetUtcNow().UtcDateTime.AddDays(-Math.Max(days, 1));
        var files = new DirectoryInfo(folder).GetFiles("pdv-*.backup").Where(f => BackupName().IsMatch(f.Name))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        foreach (var old in files.Skip(KeepAtLeast).Where(f => f.CreationTimeUtc < limit))
        {
            try { old.Delete(); }
            catch (IOException ex) { log.LogWarning(ex, "Não foi possível apagar o backup antigo {Name}", old.Name); }
        }
    }

    private State LoadState()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(SettingsPath)) ?? new State() : new State(); }
        catch (JsonException) { return new State(); }
    }

    private void SaveState(State state)
    {
        Directory.CreateDirectory(server.Folder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(state, Json));
    }

    private sealed record State(BackupSettings? Settings = null, DateTimeOffset? LastBackupAt = null, string? LastError = null);
}

/// <summary>Retaguarda › Sistema › Backup.</summary>
public sealed class BackupManager(
    BackupRunner runner,
    BackupService backups,
    PermissionChecker permissions,
    MaintenanceGate maintenance,
    TerminalGate terminals,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<BackupManager> log) : IBackupManager
{
    public const string RestoreConfirmation = "RESTAURAR";

    public async Task<BackupStatus> GetStatusAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        var (last, error) = runner.LastRun();
        return new BackupStatus(runner.LoadSettings(), runner.Folder, last, error, runner.List());
    }

    public async Task<Result<BackupFileInfo>> BackupNowAsync(Guid actorId, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        try
        {
            return Result<BackupFileInfo>.Ok(await runner.RunAsync(null, ct));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return Result<BackupFileInfo>.Fail($"O backup falhou: {ex.Message}");
        }
    }

    public async Task<Result<BackupStatus>> SaveSettingsAsync(Guid actorId, BackupSettings settings, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        if (settings.RetentionDays is < 1 or > 3650) return Result<BackupStatus>.Fail("Retenção inválida (1 a 3650 dias).");
        var copy = string.IsNullOrWhiteSpace(settings.CopyFolder) ? null : settings.CopyFolder.Trim();
        if (copy is not null)
        {
            if (!Path.IsPathFullyQualified(copy)) return Result<BackupStatus>.Fail("Informe o caminho completo da segunda pasta (ex.: E:\\Backup ou \\\\NAS\\pdv).");
            try
            {
                // Testa a gravação agora, para o problema aparecer na configuração e não na madrugada.
                Directory.CreateDirectory(copy);
                var probe = Path.Combine(copy, ".teste-pdv");
                await File.WriteAllTextAsync(probe, "ok", ct);
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Result<BackupStatus>.Fail($"O servidor não conseguiu gravar em \"{copy}\": {ex.Message}");
            }
        }
        runner.SaveSettings(settings with { CopyFolder = copy });
        return Result<BackupStatus>.Ok(await GetStatusAsync(actorId, ct));
    }

    public async Task<Result<string>> RestoreAsync(Guid actorId, string fileName, string confirmation, CancellationToken ct = default)
    {
        await RequireAsync(actorId, ct);
        if (!string.Equals(confirmation?.Trim(), RestoreConfirmation, StringComparison.Ordinal))
            return Result<string>.Fail($"Para confirmar, digite {RestoreConfirmation}.");
        var path = runner.Resolve(fileName);
        if (path is null) return Result<string>.Fail("Backup não encontrado.");

        if (!await maintenance.BeginAsync("restauração de backup", TimeSpan.FromSeconds(30), ct))
            return Result<string>.Fail("Há operações em andamento nos caixas. Tente de novo em instantes.");
        try
        {
            return await runner.ExclusiveAsync(() => RestoreCoreAsync(actorId, fileName, path, ct), ct);
        }
        finally
        {
            terminals.Forget();
            maintenance.End();
        }
    }

    private async Task<Result<string>> RestoreCoreAsync(Guid actorId, string fileName, string path, CancellationToken ct)
    {
        IReadOnlyList<TerminalKey> keys;
        await using (var scope = scopes.CreateAsyncScope())
            keys = await scope.ServiceProvider.GetRequiredService<TerminalRegistry>().ExportKeysAsync(ct);

        // Backup de segurança do estado atual: permite desfazer a restauração.
        var safety = Path.Combine(runner.Folder, $"pdv-{clock.GetLocalNow():yyyyMMdd-HHmmss}-antes-de-restaurar.backup");
        Directory.CreateDirectory(runner.Folder);
        await backups.BackupAsync(safety, ct);

        try
        {
            await backups.RestoreAsync(path, ct);
            await AfterRestoreAsync(keys, actorId, fileName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Restauração de {Name} falhou; voltando ao estado anterior", fileName);
            await backups.RestoreAsync(safety, CancellationToken.None);
            await AfterRestoreAsync(keys, null, null, CancellationToken.None);
            return Result<string>.Fail(ex is DatabaseNewerThanAppException
                ? ex.Message
                : $"A restauração falhou e o banco voltou ao estado anterior: {ex.Message}");
        }

        log.LogWarning("Banco restaurado a partir de {Name} (backup de segurança: {Safety})", fileName, Path.GetFileName(safety));
        return Result<string>.Ok($"Banco restaurado a partir de {fileName}.\nO estado anterior foi guardado em {Path.GetFileName(safety)}.");
    }

    /// <summary>Atualiza o esquema (backup de versão anterior) e mantém conectados os caixas pareados depois do backup.</summary>
    private async Task AfterRestoreAsync(IReadOnlyList<TerminalKey> keys, Guid? actorId, string? fileName, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(ct);
        await scope.ServiceProvider.GetRequiredService<TerminalRegistry>().ImportKeysAsync(keys, ct);
        if (fileName is null) return;
        var db = scope.ServiceProvider.GetRequiredService<IPdvDbContext>();
        // O usuário pode não existir no backup restaurado: a auditoria guarda o ID só como referência.
        db.AuditEntries.Add(new AuditEntry(AuditActions.BackupRestored, null, null, actorId, fileName, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }

    private async Task RequireAsync(Guid actorId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageSystem, ct))
            throw new DomainException("Seu usuário não tem permissão para administrar o sistema.");
    }
}

/// <summary>Backup diário no horário configurado; se o servidor estava desligado no horário, faz assim que puder.</summary>
public sealed class BackupScheduler(BackupRunner runner, TimeProvider clock, ILogger<BackupScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); // deixa o servidor e os caixas subirem primeiro
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsDue(runner.LoadSettings(), runner.LastRun().LastBackupAt, clock.GetLocalNow()))
                    await runner.RunAsync(null, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Backup automático falhou");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    public static bool IsDue(BackupSettings settings, DateTimeOffset? last, DateTimeOffset now)
    {
        if (!settings.Enabled) return false;
        if (last is null) return true;
        var scheduledToday = new DateTimeOffset(now.Date.Add(settings.DailyAt.ToTimeSpan()), now.Offset);
        if (now >= scheduledToday && last < scheduledToday) return true;
        return now - last > TimeSpan.FromHours(26); // horário perdido (servidor desligado à noite)
    }
}

/// <summary>Transmite notas em contingência ou sem resposta a cada 2 minutos, sem depender de um caixa vender.</summary>
public sealed class NfceQueueWorker(IServiceScopeFactory scopes, ILogger<NfceQueueWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<Application.Fiscal.NfceService>().ProcessPendingAsync(ct: stoppingToken);
                if (sent > 0) log.LogInformation("{Count} NFC-e pendente(s) transmitida(s)", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Falha ao transmitir NFC-e pendentes");
            }
        }
    }
}

public sealed class ServerAdmin(ServerSettings settings, PermissionChecker permissions) : IServerAdmin
{
    public async Task<ServerInfo> GetInfoAsync(Guid actorId, CancellationToken ct = default)
    {
        var allowed = await permissions.HasAsync(actorId, Permissions.ManageSystem, ct);
        return new ServerInfo(Application.Remote.RemoteApi.Version, Environment.MachineName, settings.Folder, ServerApp.StartedAt,
            allowed ? ServerSettings.Format(settings.PairingCode) : null);
    }

    public async Task<Result<string>> RegeneratePairingCodeAsync(Guid actorId, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageSystem, ct))
            return Result<string>.Fail("Seu usuário não tem permissão para administrar o sistema.");
        return Result<string>.Ok(ServerSettings.Format(settings.RegeneratePairingCode()));
    }
}

public sealed class DemoDataSeeder(IConfiguration configuration, DatabaseInitializer initializer) : IDemoDataSeeder
{
    public Task SeedAsync(Guid userId, CancellationToken ct = default) =>
        configuration.GetValue<bool>("Pdv:SeedDemoData") ? initializer.SeedDemoDataAsync(userId, ct) : Task.CompletedTask;
}
