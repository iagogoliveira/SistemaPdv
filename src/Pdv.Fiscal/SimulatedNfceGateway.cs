using System.Collections.Concurrent;
using System.Globalization;
using Pdv.Application.Fiscal;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;

namespace Pdv.Fiscal;

/// <summary>
/// SEFAZ simulada para desenvolvimento, demonstração e treinamento (Pdv:Fiscal:Simulator = true).
/// Monta e assina o XML real (DFe.NET), mas nada sai do computador: a autorização é respondida localmente.
/// Recusa operar em PRODUÇÃO, para nunca produzir uma nota "autorizada" falsa com valor fiscal.
/// </summary>
public sealed class SimulatedNfceGateway(INfceGateway xmlBuilder) : INfceGateway
{
    private readonly ConcurrentDictionary<string, string> _authorized = new();
    private long _protocol = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public SignedNfce BuildAndSign(NfceDraft draft, NfceSettings settings)
    {
        EnsureHomologation(settings);
        return xmlBuilder.BuildAndSign(draft, settings);
    }

    public Task<SefazResult> AuthorizeAsync(string signedXml, NfceSettings settings, string uf, CancellationToken ct)
    {
        EnsureHomologation(settings);
        var key = ExtractKey(signedXml);
        var protocol = _authorized.GetOrAdd(key, _ => "9" + Interlocked.Increment(ref _protocol).ToString("00000000000000", CultureInfo.InvariantCulture));
        return Task.FromResult(new SefazResult(SefazOutcome.Authorized, 100, "Autorizado o uso da NF-e (SEFAZ SIMULADA)", protocol,
            signedXml, DateTimeOffset.UtcNow));
    }

    public Task<SefazResult> QueryAsync(string accessKey, NfceSettings settings, string uf, CancellationToken ct) =>
        Task.FromResult(_authorized.TryGetValue(accessKey, out var protocol)
            ? new SefazResult(SefazOutcome.Authorized, 100, "Autorizado o uso da NF-e (SEFAZ SIMULADA)", protocol)
            : new SefazResult(SefazOutcome.NotFound, 217, "NF-e não consta na base (SEFAZ SIMULADA)"));

    public Task<SefazResult> CancelAsync(string accessKey, string protocol, string cnpj, string justification, NfceSettings settings,
        string uf, CancellationToken ct)
    {
        EnsureHomologation(settings);
        return Task.FromResult(new SefazResult(SefazOutcome.Authorized, 135, "Evento registrado (SEFAZ SIMULADA)", "8" + protocol));
    }

    public Task<SefazResult> VoidNumbersAsync(string cnpj, int series, long firstNumber, long lastNumber, string justification,
        NfceSettings settings, string uf, CancellationToken ct)
    {
        EnsureHomologation(settings);
        return Task.FromResult(new SefazResult(SefazOutcome.Authorized, 102, "Inutilização de número homologado (SEFAZ SIMULADA)",
            "7" + Interlocked.Increment(ref _protocol).ToString("00000000000000", CultureInfo.InvariantCulture)));
    }

    public Task<SefazResult> ServiceStatusAsync(NfceSettings settings, string uf, CancellationToken ct) =>
        Task.FromResult(new SefazResult(SefazOutcome.Authorized, 107, "Serviço em operação (SEFAZ SIMULADA — nada é enviado)"));

    private static void EnsureHomologation(NfceSettings settings)
    {
        if (settings.Environment != FiscalEnvironment.Homologation)
            throw new DomainException("A SEFAZ simulada só pode ser usada em homologação. Desative Pdv:Fiscal:Simulator para produção.");
    }

    private static string ExtractKey(string xml)
    {
        var start = xml.IndexOf("Id=\"NFe", StringComparison.Ordinal) + 7;
        return xml.Substring(start, 44);
    }
}
