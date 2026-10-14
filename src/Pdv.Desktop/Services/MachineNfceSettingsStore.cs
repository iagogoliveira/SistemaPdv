using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pdv.Application.Fiscal;
using Pdv.Domain.Fiscal;

namespace Pdv.Desktop.Services;

/// <summary>
/// Configuração de NFC-e deste computador em %ProgramData%\SistemaPDV\nfce.json.
/// Senha do certificado e CSC são cifrados com DPAPI (escopo da máquina): o arquivo copiado
/// para outro computador não revela os segredos. O .pfx é copiado para a mesma pasta.
/// </summary>
/// <param name="filePath">Outro arquivo (testes/treinamento); vazio = %ProgramData%\SistemaPDV\nfce.json.</param>
public sealed class MachineNfceSettingsStore(string? filePath = null) : INfceSettingsStore
{
    private static readonly byte[] Entropy = "SistemaPDV.NFCe.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultFilePath { get; } = Path.Combine(LocalSettings.Folder, "nfce.json");

    private string FilePath { get; } = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath;

    public static string CertificateCopyPath { get; } = Path.Combine(LocalSettings.Folder, "certificado-a1.pfx");

    public NfceSettings Load()
    {
        if (!File.Exists(FilePath))
            return new NfceSettings(false, FiscalEnvironment.Homologation, 1, null, null, null, null);
        var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) ?? new Stored();
        return new NfceSettings(stored.Enabled, stored.Environment, stored.Series, stored.CertificatePath,
            Unprotect(stored.CertificatePassword), stored.CscId, Unprotect(stored.CscToken), stored.TimeoutSeconds,
            stored.CancellationWindowMinutes);
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
            CertificatePassword = Protect(settings.CertificatePassword),
            CscId = settings.CscId,
            CscToken = Protect(settings.CscToken),
            TimeoutSeconds = settings.TimeoutSeconds,
            CancellationWindowMinutes = settings.CancellationWindowMinutes,
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, Json));
    }

    /// <summary>Copia o .pfx escolhido para a pasta do sistema (o original pode estar num pendrive).</summary>
    public static string ImportCertificate(string sourcePath)
    {
        Directory.CreateDirectory(LocalSettings.Folder);
        File.Copy(sourcePath, CertificateCopyPath, overwrite: true);
        return CertificateCopyPath;
    }

    private static string? Protect(string? value) =>
        string.IsNullOrEmpty(value) ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (CryptographicException)
        {
            return null; // arquivo de outro computador: segredo precisa ser informado de novo
        }
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
