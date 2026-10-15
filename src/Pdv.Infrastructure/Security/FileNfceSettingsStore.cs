using System.Text.Json;
using Pdv.Application.Fiscal;
using Pdv.Application.Security;
using Pdv.Domain.Fiscal;

namespace Pdv.Infrastructure.Security;

/// <summary>
/// Configuração de NFC-e do servidor em {pasta de dados}\nfce.json. Senha do certificado e CSC são cifrados
/// com DPAPI (escopo da máquina): o arquivo copiado para outro computador não revela os segredos.
/// O .pfx fica na mesma pasta.
/// </summary>
/// <param name="folder">Pasta de dados do servidor.</param>
/// <param name="filePath">Outro arquivo de configuração (testes/treinamento); vazio = {folder}\nfce.json.</param>
public sealed class FileNfceSettingsStore(string folder, string? filePath = null) : INfceSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const string Purpose = "SistemaPDV.NFCe.v1";

    private string FilePath { get; } = string.IsNullOrWhiteSpace(filePath) ? Path.Combine(folder, "nfce.json") : filePath;

    public NfceSettings Load()
    {
        if (!File.Exists(FilePath))
            return new NfceSettings(false, FiscalEnvironment.Homologation, 1, null, null, null, null);
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) ?? new Stored();
        return new NfceSettings(stored.Enabled, stored.Environment, stored.Series, stored.CertificatePath,
            Dpapi.Unprotect(stored.CertificatePassword, Purpose), stored.CscId, Dpapi.Unprotect(stored.CscToken, Purpose),
            stored.TimeoutSeconds, stored.CancellationWindowMinutes);
    }

    public void Save(NfceSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var stored = new Stored
        {
            Enabled = settings.Enabled,
            Environment = settings.Environment,
            Series = settings.Series,
            CertificatePath = settings.CertificatePath,
            CertificatePassword = Dpapi.Protect(settings.CertificatePassword, Purpose),
            CscId = settings.CscId,
            CscToken = Dpapi.Protect(settings.CscToken, Purpose),
            TimeoutSeconds = settings.TimeoutSeconds,
            CancellationWindowMinutes = settings.CancellationWindowMinutes,
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, Json));
    }

    public string ImportCertificate(byte[] pfx)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "certificado-a1.pfx");
        File.WriteAllBytes(path, pfx);
        return path;
    }

    private sealed class Stored
    {
        public bool Enabled { get; set; }
        public FiscalEnvironment Environment { get; set; } = FiscalEnvironment.Homologation;
        public int Series { get; set; } = 1;
        public string? CertificatePath { get; set; }
        public string? CertificatePassword { get; set; }
        public string? CscId { get; set; }
        public string? CscToken { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
        public int CancellationWindowMinutes { get; set; } = 30;
    }
}
