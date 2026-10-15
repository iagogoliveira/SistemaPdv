using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Pdv.Application.Remote;

namespace Pdv.Server;

/// <summary>
/// Atualização automática dos caixas: o instalador da versão do servidor fica em {pasta de dados}\atualizacao
/// (o instalador do computador principal copia a si mesmo para lá). Caixas desatualizados baixam e instalam.
/// </summary>
public sealed partial class UpdateCatalog(ServerSettings settings)
{
    private readonly Lock _gate = new();
    private (string Path, DateTime Written, string Sha256)? _cache;

    public string Folder => System.IO.Path.Combine(settings.Folder, "atualizacao");

    [GeneratedRegex(@"^SistemaPDV-Setup-(\d+\.\d+\.\d+)\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex SetupName();

    /// <summary>Instalador da mesma versão do servidor, se houver.</summary>
    public UpdateInfo? Find()
    {
        if (!Directory.Exists(Folder)) return null;
        var file = new DirectoryInfo(Folder).GetFiles("SistemaPDV-Setup-*.exe")
            .FirstOrDefault(f => SetupName().Match(f.Name) is { Success: true } m && m.Groups[1].Value == RemoteApi.Version);
        if (file is null) return null;
        lock (_gate)
        {
            if (_cache is not { } c || c.Path != file.FullName || c.Written != file.LastWriteTimeUtc)
            {
                using var stream = file.OpenRead();
                _cache = (file.FullName, file.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
            }
            return new UpdateInfo(RemoteApi.Version, file.Name, file.Length, _cache.Value.Sha256);
        }
    }

    public string? PackagePath(UpdateInfo info) => System.IO.Path.Combine(Folder, info.FileName);
}
