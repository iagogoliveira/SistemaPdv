using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Pdv.Desktop.Services;

/// <summary>
/// Configuração da máquina em %ProgramData%\SistemaPDV, que sobrepõe o appsettings.json do programa
/// (Arquivos de Programas, sem permissão de escrita):
/// connection.json — servidor SQL, gravado pelo instalador e pela tela de conexão;
/// appsettings.json — demais ajustes da máquina (impressora etc.), editado pelo técnico.
/// </summary>
public static class LocalSettings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SistemaPDV");

    public static string SettingsFilePath { get; } = Path.Combine(Folder, "appsettings.json");

    public static string ConnectionFilePath { get; } = Path.Combine(Folder, "connection.json");

    public static string BuildConnectionString(string server, string database = "SistemaPdv") =>
        new SqlConnectionStringBuilder
        {
            DataSource = server.Trim(),
            InitialCatalog = database,
            IntegratedSecurity = true,
            TrustServerCertificate = true,
            ConnectTimeout = 10,
        }.ConnectionString;

    public static string? CurrentServer(string? connectionString)
    {
        try { return connectionString is null ? null : new SqlConnectionStringBuilder(connectionString).DataSource; }
        catch (ArgumentException) { return null; }
    }

    public static void SaveConnectionString(string connectionString)
    {
        Directory.CreateDirectory(Folder);
        var root = new JsonObject { ["ConnectionStrings"] = new JsonObject { ["Pdv"] = connectionString } };
        File.WriteAllText(ConnectionFilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
