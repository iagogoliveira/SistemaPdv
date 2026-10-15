namespace Pdv.Application.Maintenance;

public sealed record BackupSettings(bool Enabled, TimeOnly DailyAt, int RetentionDays, string? CopyFolder)
{
    public static BackupSettings Default { get; } = new(true, new TimeOnly(22, 0), 30, null);
}

public sealed record BackupFileInfo(string Name, DateTimeOffset CreatedAt, long SizeBytes, bool CopiedToSecondFolder);

public sealed record BackupStatus(BackupSettings Settings, string Folder, DateTimeOffset? LastBackupAt, string? LastError,
    IReadOnlyList<BackupFileInfo> Files);

/// <summary>Backups do banco, executados no servidor (pg_dump). Implementado no Pdv.Server.</summary>
public interface IBackupManager
{
    Task<BackupStatus> GetStatusAsync(Guid actorId, CancellationToken ct = default);

    Task<Result<BackupFileInfo>> BackupNowAsync(Guid actorId, CancellationToken ct = default);

    Task<Result<BackupStatus>> SaveSettingsAsync(Guid actorId, BackupSettings settings, CancellationToken ct = default);

    /// <summary>
    /// Substitui o banco pelo backup (confirmação: digitar RESTAURAR). Antes faz um backup de segurança;
    /// os caixas conectados continuam conectados.
    /// </summary>
    Task<Result<string>> RestoreAsync(Guid actorId, string fileName, string confirmation, CancellationToken ct = default);
}

public sealed record ServerInfo(string Version, string MachineName, string DataFolder, DateTimeOffset StartedAt, string? PairingCode);

/// <summary>Informações do servidor e código para conectar novos caixas. Implementado no Pdv.Server.</summary>
public interface IServerAdmin
{
    Task<ServerInfo> GetInfoAsync(Guid actorId, CancellationToken ct = default);

    Task<Result<string>> RegeneratePairingCodeAsync(Guid actorId, CancellationToken ct = default);

    /// <summary>Pacote .zip para o suporte: logs do servidor (7 dias) e resumo técnico, sem senhas, chaves ou certificados.</summary>
    Task<byte[]> ExportSupportPackageAsync(Guid actorId, CancellationToken ct = default);
}

/// <summary>Produtos de demonstração; o servidor só os cria quando configurado para isso (desenvolvimento/treinamento).</summary>
public interface IDemoDataSeeder
{
    Task SeedAsync(Guid userId, CancellationToken ct = default);
}

public enum SetupState
{
    Ok,
    Warning,
    Pending,
}

/// <summary>Item do assistente de implantação: situação, o que fazer e em qual aba da retaguarda.</summary>
public sealed record SetupItem(string Key, string Title, SetupState State, string Detail, string Tab);

public sealed record SetupChecklist(IReadOnlyList<SetupItem> Items)
{
    public int Ready => Items.Count(i => i.State == SetupState.Ok);
    public int Pending => Items.Count(i => i.State == SetupState.Pending);
    public bool Complete => Items.All(i => i.State == SetupState.Ok);
}

/// <summary>
/// Assistente de implantação da loja: confere empresa, tributação, produtos, tabela IBPT, certificado, CSC, NFC-e,
/// comunicação com a SEFAZ, usuários, caixas, licença e backup. Implementado no Pdv.Server.
/// </summary>
public interface ISetupAssistant
{
    Task<SetupChecklist> GetAsync(Guid actorId, CancellationToken ct = default);

    /// <summary>Registra que o contador conferiu os perfis tributários.</summary>
    Task<SetupChecklist> ConfirmTaxReviewAsync(Guid actorId, CancellationToken ct = default);

    /// <summary>Consulta o status da SEFAZ com o certificado configurado (sem emitir nota) e guarda o resultado.</summary>
    Task<Result<string>> TestSefazAsync(Guid actorId, CancellationToken ct = default);
}
