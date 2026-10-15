using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Pdv.Application.Security;

namespace Pdv.Server;

/// <summary>
/// Configuração do servidor em {pasta de dados}\server.json (padrão: %ProgramData%\SistemaPDV\Servidor).
/// Na primeira leitura a senha do banco e o código de conexão de caixas são cifrados com DPAPI no próprio arquivo:
/// o instalador grava a senha em texto e o servidor a protege ao iniciar.
/// </summary>
public sealed class ServerSettings
{
    private const string Purpose = "SistemaPDV.Server.v1";
    private readonly Lock _gate = new();

    private ServerSettings(string folder) => Folder = folder;

    public string Folder { get; }

    public string FilePath => Path.Combine(Folder, "server.json");

    public string ConnectionString { get; private set; } = "";

    public string PairingCode { get; private set; } = "";

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SistemaPDV", "Servidor");

    /// <summary>Lê (e protege) o server.json. <paramref name="fallbackConnectionString"/> vem do appsettings/variáveis de ambiente.</summary>
    public static ServerSettings Load(string folder, string? fallbackConnectionString)
    {
        var settings = new ServerSettings(folder);
        Directory.CreateDirectory(folder);
        var root = File.Exists(settings.FilePath)
            ? JsonNode.Parse(File.ReadAllText(settings.FilePath)) as JsonObject ?? []
            : [];

        var changed = false;
        var stored = root["ConnectionStrings"]?["Pdv"]?.GetValue<string>();
        if (stored is not null)
        {
            var builder = new NpgsqlConnectionStringBuilder(stored);
            if (!string.IsNullOrEmpty(builder.Password) && !Dpapi.IsProtected(builder.Password))
            {
                builder.Password = Dpapi.Protect(builder.Password, Purpose);
                root["ConnectionStrings"]!["Pdv"] = builder.ConnectionString;
                changed = true;
            }
            var plain = new NpgsqlConnectionStringBuilder(builder.ConnectionString);
            if (Dpapi.IsProtected(plain.Password))
                plain.Password = Dpapi.Unprotect(plain.Password, Purpose)
                    ?? throw new InvalidOperationException("A senha do banco em server.json foi cifrada em outro computador. Informe-a de novo.");
            settings.ConnectionString = plain.ConnectionString;
        }
        else
        {
            settings.ConnectionString = fallbackConnectionString
                ?? throw new InvalidOperationException($"Conexão com o banco não configurada ({settings.FilePath}).");
        }

        var code = Dpapi.Unprotect(root["Pdv"]?["Server"]?[nameof(PairingCode)]?.GetValue<string>(), Purpose);
        if (string.IsNullOrEmpty(code))
        {
            code = NewPairingCode();
            SetPairingCode(root, code);
            changed = true;
        }
        settings.PairingCode = code;

        if (changed) Write(settings.FilePath, root);
        return settings;
    }

    public string RegeneratePairingCode()
    {
        lock (_gate)
        {
            var root = File.Exists(FilePath) ? JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? [] : [];
            var code = NewPairingCode();
            SetPairingCode(root, code);
            Write(FilePath, root);
            PairingCode = code;
            return code;
        }
    }

    /// <summary>Compara em tempo constante, aceitando "1234-5678" ou "12345678".</summary>
    public bool MatchesPairingCode(string? candidate)
    {
        var normalized = new string((candidate ?? "").Where(char.IsDigit).ToArray());
        return normalized.Length == PairingCode.Length
            && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(normalized), System.Text.Encoding.ASCII.GetBytes(PairingCode));
    }

    public static string Format(string code) => code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;

    private static string NewPairingCode() =>
        RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("00000000", CultureInfo.InvariantCulture);

    private static void SetPairingCode(JsonObject root, string code)
    {
        if (root["Pdv"] is not JsonObject pdv) root["Pdv"] = pdv = [];
        if (pdv["Server"] is not JsonObject server) pdv["Server"] = server = [];
        server[nameof(PairingCode)] = Dpapi.Protect(code, Purpose);
    }

    private static void Write(string path, JsonObject root)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
