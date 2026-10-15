using System.Text.Json;
using System.Text.Json.Nodes;
using Pdv.Application.Remote;
using Pdv.Application.Sales;
using Pdv.Application.Security;

namespace Pdv.Client;

/// <summary>Endereço do servidor e chave de acesso deste caixa (cifrada com DPAPI), em {pasta}\connection.json.</summary>
public sealed class TerminalConnectionStore(string folder)
{
    private const string Purpose = "SistemaPDV.Terminal.v1";

    public string FilePath { get; } = Path.Combine(folder, "connection.json");

    public (string? ServerAddress, string? AccessKey) Load()
    {
        if (!File.Exists(FilePath)) return (null, null);
        try
        {
            var server = JsonNode.Parse(File.ReadAllText(FilePath))?["Pdv"]?["Server"];
            return (server?["Address"]?.GetValue<string>(), Dpapi.Unprotect(server?["AccessKey"]?.GetValue<string>(), Purpose));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public void Save(string serverAddress, string? accessKey)
    {
        Directory.CreateDirectory(folder);
        var root = new JsonObject
        {
            ["Pdv"] = new JsonObject
            {
                ["Server"] = new JsonObject { ["Address"] = serverAddress, ["AccessKey"] = Dpapi.Protect(accessKey, Purpose) },
            },
        };
        AtomicFile.WriteAllText(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Diário da venda em conclusão: gravado no disco antes de enviar ao servidor e apagado quando o servidor responde.
/// Se o caixa desligar no meio do caminho, na volta o sistema confere no servidor (pela chave de idempotência)
/// se a venda foi gravada — sem duplicar nem perder a venda.
/// </summary>
public sealed class SaleJournal(string folder)
{
    public string FilePath { get; } = Path.Combine(folder, "venda-em-andamento.json");

    public void Save(FinalizeSaleCommand command) =>
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(command, RemoteApi.Json));

    public FinalizeSaleCommand? Load()
    {
        if (!File.Exists(FilePath)) return null;
        try { return JsonSerializer.Deserialize<FinalizeSaleCommand>(File.ReadAllText(FilePath), RemoteApi.Json); }
        catch (JsonException) { return null; }
    }

    public void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}

internal static class AtomicFile
{
    /// <summary>Grava num arquivo temporário com flush no disco e só então substitui: queda de energia não deixa arquivo pela metade.</summary>
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
