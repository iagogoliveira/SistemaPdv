using System.IO.Compression;
using System.Security.Cryptography;
using Pdv.Application.Maintenance;
using Pdv.Application.Remote;
using Pdv.Client;
using Pdv.Domain.Common;

namespace Pdv.Server.Tests;

[Collection(nameof(ServerGroup))]
public class Stage7Tests(ServerFixture fx)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Paired_terminals_download_the_installer_and_check_its_hash()
    {
        var t = await fx.PairAsync("CAIXA-ATUALIZA");
        var folder = Path.Combine(fx.Folder, "atualizacao");
        Directory.CreateDirectory(folder);
        var setup = Path.Combine(folder, $"SistemaPDV-Setup-{RemoteApi.Version}.exe");
        var content = RandomNumberGenerator.GetBytes(300_000);
        await File.WriteAllBytesAsync(setup, content, Ct);
        await File.WriteAllBytesAsync(Path.Combine(folder, "SistemaPDV-Setup-0.0.1.exe"), [1, 2, 3], Ct); // outra versão: ignorada

        var info = await t.Client.GetUpdateInfoAsync(Ct);
        Assert.Equal(RemoteApi.Version, info.Version);
        Assert.Equal(content.Length, info.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), info.Sha256);

        var downloaded = await t.Client.DownloadUpdateAsync(info, Path.Combine(fx.Folder, "baixado"), ct: Ct);
        Assert.Equal(content, await File.ReadAllBytesAsync(downloaded, Ct));

        // Hash diferente (arquivo trocado no meio do caminho): o download é recusado e apagado.
        var tampered = info with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<ServerException>(() => t.Client.DownloadUpdateAsync(tampered, Path.Combine(fx.Folder, "baixado2"), ct: Ct));
        Assert.False(File.Exists(Path.Combine(fx.Folder, "baixado2", info.FileName)));

        // Só caixas conectados baixam.
        await Assert.ThrowsAsync<TerminalNotPairedException>(() => fx.NewClient().GetUpdateInfoAsync(Ct));
        File.Delete(setup);
        await Assert.ThrowsAsync<ServerException>(() => t.Client.GetUpdateInfoAsync(Ct));
    }

    [Fact]
    public async Task Support_package_has_logs_and_summary_without_secrets()
    {
        var t = await fx.PairAsync("CAIXA-SUPORTE");
        await Assert.ThrowsAsync<DomainException>(() =>
            t.Runner.RunAsync<IServerAdmin, byte[]>(s => s.ExportSupportPackageAsync(Guid.NewGuid(), default)));

        var bytes = await t.Runner.RunAsync<IServerAdmin, byte[]>(s => s.ExportSupportPackageAsync(fx.AdminUserId, default));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var summaryEntry = zip.GetEntry("resumo-servidor.txt")!;
        using var reader = new StreamReader(summaryEntry.Open());
        var summary = await reader.ReadToEndAsync(Ct);
        Assert.Contains("CAIXA-SUPORTE", summary);
        Assert.Contains("Licença: Active", summary);
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("logs-servidor/", StringComparison.Ordinal));

        // Nada de segredos: senha do banco, código de conexão, chaves.
        var all = string.Join("\n", zip.Entries.Select(e => { using var r = new StreamReader(e.Open()); return r.ReadToEnd(); }));
        Assert.DoesNotContain("pdv_dev", all);
        Assert.DoesNotContain(fx.PairingCode, all);
        Assert.DoesNotContain(t.AccessKey, all);
        Assert.DoesNotContain("PRIVATE KEY", all);
    }

    [Fact]
    public void Esc_pos_commands_match_the_printer_protocol()
    {
        Assert.Equal([0x1B, 0x70, 0x00, 0x19, 0xFA], EscPos.OpenDrawer());
        Assert.Equal([0x1B, 0x70, 0x01, 0x19, 0xFA], EscPos.OpenDrawer(1));
        Assert.Equal([0x1D, 0x56, 0x42, 0x03], EscPos.Cut());
        Assert.Equal([0x1B, 0x40], EscPos.Initialize());
    }
}
