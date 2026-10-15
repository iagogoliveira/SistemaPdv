using System.Diagnostics;
using Npgsql;

namespace Pdv.Infrastructure.Persistence;

/// <summary>
/// Backup/restauração do banco com as ferramentas oficiais do PostgreSQL (formato custom do pg_dump,
/// compactado e restaurável com pg_restore).
/// </summary>
public sealed class BackupService(string connectionString, IPgTools tools)
{
    public Task BackupAsync(string backupFilePath, CancellationToken ct = default) =>
        tools.DumpAsync(new NpgsqlConnectionStringBuilder(connectionString), backupFilePath, ct);

    /// <summary>Substitui o banco pelo conteúdo do backup. Desconecta os demais usuários.</summary>
    public async Task RestoreAsync(string backupFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(backupFilePath)) throw new FileNotFoundException("Arquivo de backup não encontrado.", backupFilePath);

        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var database = target.Database ?? throw new InvalidOperationException("A conexão não informa o banco.");
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };

        NpgsqlConnection.ClearAllPools();
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync(ct);
            var quoted = "\"" + database.Replace("\"", "\"\"") + "\"";
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {quoted} WITH (FORCE); CREATE DATABASE {quoted};";
            await command.ExecuteNonQueryAsync(ct);
        }

        await tools.RestoreAsync(target, backupFilePath, ct);
        NpgsqlConnection.ClearAllPools();
    }
}

/// <summary>Executa pg_dump/pg_restore. Separado para permitir outra origem das ferramentas (ex.: contêiner nos testes).</summary>
public interface IPgTools
{
    Task DumpAsync(NpgsqlConnectionStringBuilder connection, string filePath, CancellationToken ct);
    Task RestoreAsync(NpgsqlConnectionStringBuilder connection, string filePath, CancellationToken ct);
}

/// <summary>Ferramentas instaladas na máquina (pasta bin do PostgreSQL ou PATH).</summary>
public sealed class LocalPgTools : IPgTools
{
    public Task DumpAsync(NpgsqlConnectionStringBuilder connection, string filePath, CancellationToken ct) =>
        RunAsync("pg_dump", connection, ["--format=custom", "--no-owner", $"--file={filePath}", $"--dbname={connection.Database}"], ct);

    public Task RestoreAsync(NpgsqlConnectionStringBuilder connection, string filePath, CancellationToken ct) =>
        RunAsync("pg_restore", connection, ["--no-owner", "--exit-on-error", $"--dbname={connection.Database}", filePath], ct);

    /// <summary>Procura no PATH e em "C:\Program Files\PostgreSQL\{versão}\bin", da versão mais nova para a mais antiga.</summary>
    public static string? Find(string tool)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exe));

        // PostgreSQL embutido pelo instalador do PDV ({app}\PostgreSQL\bin, ao lado da pasta do servidor).
        var bundled = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "PostgreSQL", "bin", exe));
        candidates = candidates.Prepend(bundled);

        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        if (Directory.Exists(installRoot))
        {
            var versions = Directory.GetDirectories(installRoot)
                .OrderByDescending(d => int.TryParse(Path.GetFileName(d), out var v) ? v : 0)
                .Select(d => Path.Combine(d, "bin", exe));
            candidates = versions.Concat(candidates);
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    internal static ProcessStartInfo StartInfo(string fileName, NpgsqlConnectionStringBuilder c, IEnumerable<string> toolArgs, IEnumerable<string>? prefix = null)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in prefix ?? []) info.ArgumentList.Add(a);
        foreach (var a in ConnectionArgs(c).Concat(toolArgs)) info.ArgumentList.Add(a);
        // Senha via variável de ambiente: não aparece na linha de comando do processo.
        if (!string.IsNullOrEmpty(c.Password)) info.Environment["PGPASSWORD"] = c.Password;
        return info;
    }

    public static async Task ExecuteAsync(ProcessStartInfo info, string description, CancellationToken ct)
    {
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Não foi possível executar {description}.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{description} falhou (código {process.ExitCode}): {(await stderr).Trim()}");
    }

    private static IEnumerable<string> ConnectionArgs(NpgsqlConnectionStringBuilder c)
    {
        yield return $"--host={c.Host}";
        yield return $"--port={c.Port}";
        if (!string.IsNullOrEmpty(c.Username)) yield return $"--username={c.Username}";
        yield return "--no-password";
    }

    private static Task RunAsync(string tool, NpgsqlConnectionStringBuilder c, string[] args, CancellationToken ct)
    {
        var path = Find(tool) ?? throw new InvalidOperationException(
            $"{tool} não encontrado. Instale o PostgreSQL (ferramentas de linha de comando) neste computador.");
        return ExecuteAsync(StartInfo(path, c, args), tool, ct);
    }
}
