using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Pdv.Application.Licensing;

namespace Pdv.Infrastructure.Security;

/// <summary>
/// Licença instalada em {pasta do servidor}\licenca.pdvlic e código da máquina, derivado do identificador da instalação
/// do Windows (MachineGuid): muda ao trocar de computador ou reinstalar o Windows, mas não com atualizações.
/// </summary>
/// <param name="machineCodeOverride">Outro código (testes e treinamento).</param>
public sealed class FileLicenseStore(string folder, string? machineCodeOverride = null) : ILicenseStore
{
    private string FilePath => Path.Combine(folder, "licenca.pdvlic");

    public string MachineCode { get; } = string.IsNullOrWhiteSpace(machineCodeOverride) ? ComputeMachineCode() : machineCodeOverride.Trim();

    public string? LoadText() => File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;

    public void Save(string licenseText)
    {
        Directory.CreateDirectory(folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, licenseText);
        File.Move(temp, FilePath, overwrite: true);
    }

    public static string ComputeMachineCode()
    {
        var id = Environment.MachineName;
        if (OperatingSystem.IsWindows())
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) id = guid;
        }
        return LicenseRules.FormatMachineCode(SHA256.HashData(Encoding.UTF8.GetBytes("SistemaPDV|" + id)));
    }
}
