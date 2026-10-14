using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace Pdv.Desktop.Services;

/// <summary>
/// Configuração da máquina em %ProgramData%\SistemaPDV, que sobrepõe o appsettings.json do programa
/// (Arquivos de Programas, sem permissão de escrita):
/// connection.json — servidor PostgreSQL, gravado pelo instalador e pela tela de conexão;
/// appsettings.json — demais ajustes da máquina (impressora etc.), editado pelo técnico.
/// </summary>
public static class LocalSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SistemaPDV");

    public static string SettingsFilePath { get; } = Path.Combine(Folder, "appsettings.json");

    public static string ConnectionFilePath { get; } = Path.Combine(Folder, "connection.json");

    /// <summary>Troca só o servidor (host[:porta]) mantendo banco, usuário e senha atuais.</summary>
    public static string WithServer(string? currentConnectionString, string server)
    {
        var builder = TryParse(currentConnectionString) ?? new NpgsqlConnectionStringBuilder { Database = "sistema_pdv", Username = "pdv" };
        var parts = server.Trim().Split(':', 2);
        builder.Host = parts[0];
        if (parts.Length == 2 && int.TryParse(parts[1], out var port)) builder.Port = port;
        builder.Timeout = 10;
        return builder.ConnectionString;
    }

    public static string? CurrentServer(string? connectionString) =>
        TryParse(connectionString) is { } b ? (b.Port == 5432 ? b.Host : $"{b.Host}:{b.Port}") : null;

    public static void SaveConnectionString(string connectionString)
    {
        Directory.CreateDirectory(Folder);
        var root = new JsonObject { ["ConnectionStrings"] = new JsonObject { ["Pdv"] = connectionString } };
        File.WriteAllText(ConnectionFilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static NpgsqlConnectionStringBuilder? TryParse(string? connectionString)
    {
        try { return connectionString is null ? null : new NpgsqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException) { return null; }
    }
}
