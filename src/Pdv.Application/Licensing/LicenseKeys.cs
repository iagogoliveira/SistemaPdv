namespace Pdv.Application.Licensing;

/// <summary>Chave pública do fornecedor, embutida no sistema. A chave privada correspondente fica só com o fornecedor.</summary>
public static class LicenseKeys
{
    public const string Production = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEReCURLSjanhrDUEDaTtfNl5xwNb9
        NcF5sIB46XRgKqD3VgnXHe23tS2IoKy6Na7QuprBR8egrG8hnN1WaEEirQ==
        -----END PUBLIC KEY-----
        """;
}
