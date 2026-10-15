using System.Xml;
using System.Xml.Schema;
using Pdv.Domain.Common;

namespace Pdv.Fiscal;

/// <summary>
/// Validação local do XML pelos esquemas oficiais da NF-e/NFC-e 4.00 (pasta Schemas, mesma versão usada pela DFe.NET).
/// Pega campo faltando, tamanho ou formato inválido antes de a nota ir à SEFAZ — evita rejeição e número "queimado".
/// </summary>
public static class NfeSchemaValidator
{
    public const string Namespace = "http://www.portalfiscal.inf.br/nfe";

    private static readonly Lazy<XmlSchemaSet> NfeSchemas = new(() => Load("nfe_v4.00.xsd"));

    public static string SchemaFolder { get; } = Path.Combine(AppContext.BaseDirectory, "Schemas");

    /// <summary>Lança <see cref="DomainException"/> com os primeiros erros encontrados.</summary>
    public static void ValidateNfe(string xml)
    {
        var errors = Validate(xml, NfeSchemas.Value);
        if (errors.Count > 0)
            throw new DomainException("O XML da NFC-e não passou na validação do esquema oficial (nada foi enviado à SEFAZ):\n" +
                string.Join("\n", errors.Take(5).Select(e => "• " + e)));
    }

    public static IReadOnlyList<string> Validate(string xml, XmlSchemaSet schemas)
    {
        var errors = new List<string>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas, DtdProcessing = DtdProcessing.Prohibit };
        settings.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error) errors.Add(Describe(e));
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        while (reader.Read()) { }
        return errors;
    }

    private static XmlSchemaSet Load(string mainSchema)
    {
        var path = Path.Combine(SchemaFolder, mainSchema);
        if (!File.Exists(path)) throw new DomainException($"Esquema oficial não encontrado: {path}. Reinstale o servidor do Sistema PDV.");
        var set = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse }))
            set.Add(Namespace, reader);
        set.Compile();
        return set;
    }

    /// <summary>Mensagem do validador com o caminho do elemento, para o suporte achar o campo.</summary>
    private static string Describe(ValidationEventArgs e)
    {
        var message = e.Message.Replace(Namespace, "", StringComparison.Ordinal).Replace("'':", "'", StringComparison.Ordinal);
        return e.Exception is { LineNumber: > 0 } x ? $"{message} (posição {x.LinePosition})" : message;
    }
}
