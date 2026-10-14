using System.Diagnostics;
using Npgsql;
using Pdv.Infrastructure.Persistence;

namespace Pdv.Infrastructure.Tests;

/// <summary>
/// pg_dump/pg_restore do contêiner de desenvolvimento (compose.yaml), para rodar o teste de backup
/// em máquinas sem o PostgreSQL instalado localmente. Os arquivos são copiados com "docker cp".
/// </summary>
internal sealed class DockerPgTools(string container) : IPgTools
{
    public static string Container => Environment.GetEnvironmentVariable("PDV_TEST_PG_CONTAINER") ?? "pdv-postgres";

    public async Task DumpAsync(NpgsqlConnectionStringBuilder connection, string filePath, CancellationToken ct)
    {
        var inside = $"/tmp/{Path.GetFileName(filePath)}";
        await Exec(connection, ["pg_dump", "--format=custom", "--no-owner", $"--file={inside}", $"--dbname={connection.Database}"], ct);
        await Docker(["cp", $"{container}:{inside}", filePath], ct);
    }

    public async Task RestoreAsync(NpgsqlConnectionStringBuilder connection, string filePath, CancellationToken ct)
    {
        var inside = $"/tmp/{Path.GetFileName(filePath)}";
        await Docker(["cp", filePath, $"{container}:{inside}"], ct);
        await Exec(connection, ["pg_restore", "--no-owner", "--exit-on-error", $"--dbname={connection.Database}", inside], ct);
    }

    public static bool IsAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", ["inspect", "-f", "{{.State.Running}}", Container])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 && output.Trim() == "true";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Dentro do contêiner o servidor é sempre localhost:5432.
    private Task Exec(NpgsqlConnectionStringBuilder c, string[] tool, CancellationToken ct) =>
        Docker(["exec", "-e", $"PGPASSWORD={c.Password}", container, .. tool, "--host=localhost", "--port=5432",
            $"--username={c.Username}", "--no-password"], ct);

    private static Task Docker(string[] args, CancellationToken ct) =>
        LocalPgTools.ExecuteAsync(new ProcessStartInfo("docker", args)
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        }, $"docker {args[0]}", ct);
}
