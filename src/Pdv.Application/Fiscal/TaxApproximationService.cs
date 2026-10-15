using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pdv.Application.Identity;
using Pdv.Domain.Auditing;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;
using Pdv.Domain.Sales;

namespace Pdv.Application.Fiscal;

public sealed record TaxTableStatus(int Rows, string? Version, string? Source, DateOnly? ValidTo, bool Expired, int ProductsWithoutRate,
    int ActiveProductsWithNcm);

public sealed record TaxTableImport(int Rows, int Skipped, string Version, DateOnly ValidTo);

/// <summary>Tributos aproximados de uma venda: por linha e no total, com o texto exigido pela Lei 12.741/2012.</summary>
public sealed record SaleApproximateTaxes(IReadOnlyDictionary<int, ApproximateTaxes> ByLine, ApproximateTaxes Total, string? Text);

/// <summary>
/// Tabela IBPT ("De olho no imposto", baixada pela empresa em deolhonoimposto.ibpt.org.br com o próprio CNPJ, um arquivo
/// .csv por UF) e cálculo do valor aproximado dos tributos exigido pela Lei 12.741/2012 no documento ao consumidor.
/// </summary>
public sealed class TaxApproximationService(IPdvDbContext db, PermissionChecker permissions, TimeProvider clock)
{
    public async Task<TaxTableStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var sample = await db.TaxApproximations.AsNoTracking().OrderByDescending(t => t.ValidTo).FirstOrDefaultAsync(ct);
        var rows = await db.TaxApproximations.CountAsync(ct);
        var ncms = await db.Products.Where(p => p.IsActive && p.Ncm != null).Select(p => p.Ncm!).Distinct().ToListAsync(ct);
        var covered = await db.TaxApproximations.Where(t => ncms.Contains(t.Ncm)).Select(t => t.Ncm).Distinct().CountAsync(ct);
        var withNcm = await db.Products.CountAsync(p => p.IsActive && p.Ncm != null, ct);
        var uncovered = ncms.Count - covered;
        var withoutRate = uncovered == 0 ? 0
            : await db.Products.CountAsync(p => p.IsActive && p.Ncm != null && !db.TaxApproximations.Any(t => t.Ncm == p.Ncm), ct);
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        return new TaxTableStatus(rows, sample?.Version, sample?.Source, sample?.ValidTo, sample is not null && sample.ValidTo < today,
            withoutRate, withNcm);
    }

    /// <summary>
    /// Importa o CSV do IBPT (codigo;ex;tipo;descricao;nacionalfederal;importadosfederal;estadual;municipal;vigenciainicio;
    /// vigenciafim;chave;versao;fonte). Só as linhas de NCM (tipo 0) interessam ao varejo. Substitui a tabela anterior.
    /// </summary>
    public async Task<Result<TaxTableImport>> ImportAsync(Guid actorId, string csv, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(actorId, Permissions.ManageCompany, ct))
            return Result<TaxTableImport>.Fail("Seu usuário não tem permissão para alterar a tributação.");
        List<TaxApproximation> rows;
        int skipped;
        try { (rows, skipped) = Parse(csv); }
        catch (DomainException ex) { return Result<TaxTableImport>.Fail(ex.Message); }
        if (rows.Count == 0) return Result<TaxTableImport>.Fail("O arquivo não tem linhas de NCM. Confira se é a tabela IBPT (.csv) da sua UF.");

        // Uma linha por NCM: quando há exceções ("ex"), vale a linha sem exceção.
        var unique = rows.GroupBy(r => r.Ncm).Select(g => g.OrderBy(r => r.Exception is null ? 0 : 1).First()).ToList();
        await db.TaxApproximations.ExecuteDeleteAsync(ct);
        db.TaxApproximations.AddRange(unique);
        var first = unique[0];
        db.AuditEntries.Add(new AuditEntry("ibpt.imported", actorId, null, null, $"{unique.Count} NCM, versão {first.Version}, até {first.ValidTo:dd/MM/yyyy}",
            clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
        return Result<TaxTableImport>.Ok(new TaxTableImport(unique.Count, skipped, first.Version, first.ValidTo));
    }

    /// <summary>Calcula os tributos aproximados da venda pelas alíquotas atuais da tabela (sem tabela: zero e sem texto).</summary>
    internal async Task<SaleApproximateTaxes> ComputeAsync(Sale sale, CancellationToken ct = default)
    {
        var ncms = sale.Items.Where(i => i.Ncm != null).Select(i => i.Ncm!).Distinct().ToList();
        var rates = await db.TaxApproximations.AsNoTracking().Where(t => ncms.Contains(t.Ncm)).ToDictionaryAsync(t => t.Ncm, ct);
        if (rates.Count == 0) return new SaleApproximateTaxes(new Dictionary<int, ApproximateTaxes>(), ApproximateTaxes.Zero, null);

        var byLine = new Dictionary<int, ApproximateTaxes>();
        var total = ApproximateTaxes.Zero;
        foreach (var item in sale.Items)
        {
            var taxes = ApproximateTaxes.Compute(item.Ncm is { } ncm ? rates.GetValueOrDefault(ncm) : null, item.Origin, item.Total);
            byLine[item.LineNumber] = taxes;
            total = total.Add(taxes);
        }
        var source = rates.Values.First();
        return new SaleApproximateTaxes(byLine, total, total.ToText($"{source.Source} {source.Version}".Trim(), CultureInfo.GetCultureInfo("pt-BR")));
    }

    internal static (List<TaxApproximation> Rows, int Skipped) Parse(string csv)
    {
        var lines = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length < 2) throw new DomainException("Arquivo vazio.");
        var header = lines[0].TrimStart('﻿').Split(';').Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int Col(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i
            : throw new DomainException($"Coluna \"{name}\" não encontrada: o arquivo não parece ser a tabela IBPT.");
        int code = Col("codigo"), ex = Col("ex"), type = Col("tipo"), description = Col("descricao"), federal = Col("nacionalfederal"),
            imported = Col("importadosfederal"), state = Col("estadual"), municipal = Col("municipal"), from = Col("vigenciainicio"),
            to = Col("vigenciafim"), version = Col("versao"), source = Col("fonte");

        var rows = new List<TaxApproximation>();
        var skipped = 0;
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var f = line.Split(';');
            if (f.Length < header.Length || f[type].Trim() != "0") { skipped++; continue; }
            var ncm = new string(f[code].Where(char.IsAsciiDigit).ToArray());
            if (ncm.Length != 8) { skipped++; continue; }
            rows.Add(new TaxApproximation(ncm, f[ex], f[description], Rate(f[federal]), Rate(f[imported]), Rate(f[state]), Rate(f[municipal]),
                Date(f[from]), Date(f[to]), f[version].Trim(), f[source].Trim().Length > 0 ? f[source].Trim() : "IBPT"));
        }
        return (rows, skipped);
    }

    private static decimal Rate(string value) =>
        decimal.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : 0;

    private static DateOnly Date(string value) =>
        DateOnly.TryParseExact(value.Trim(), ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new DomainException($"Data inválida na tabela IBPT: {value}.");

    /// <summary>As tabelas do IBPT vêm em ISO-8859-1; aceita também UTF-8.</summary>
    public static string Decode(byte[] content)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(content); }
    }
}
