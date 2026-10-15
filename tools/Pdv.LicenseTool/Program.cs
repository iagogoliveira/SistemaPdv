using System.Globalization;
using Pdv.Application.Licensing;
using Pdv.Domain.Fiscal;

// Ferramenta do FORNECEDOR do Sistema PDV (não vai para as lojas): gera o par de chaves e emite licenças.
// A chave privada nunca deve ir para o repositório nem para o computador do cliente.

var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SistemaPDV-licencas");

if (args.Length == 0 || args[0] is "-h" or "--help" or "ajuda")
{
    Console.WriteLine("""
        pdv-licenca gerar-chaves [--pasta <pasta>]
            Cria chave-privada.pem e chave-publica.pem (padrão: %USERPROFILE%\SistemaPDV-licencas).
            A chave pública vai em src/Pdv.Application/Licensing/LicenseKeys.cs; a privada fica só com o fornecedor.

        pdv-licenca emitir --cliente "Nome" --maquina XXXX-XXXX-XXXX-XXXX --caixas N
                           [--cnpj 00000000000000] [--plano "Loja"] [--validade AAAA-MM-DD]
                           [--chave <chave-privada.pem>] [--saida <arquivo.pdvlic>]
            Emite a licença assinada para o computador principal da loja.
            Sem --validade a licença é perpétua.

        pdv-licenca conferir <arquivo.pdvlic> [--publica <chave-publica.pem>]
            Mostra o conteúdo e confere a assinatura.
        """);
    return 0;
}

var options = ParseOptions(args.Skip(1).ToArray());
try
{
    switch (args[0])
    {
        case "gerar-chaves":
        {
            var folder = options.GetValueOrDefault("pasta") ?? defaultFolder;
            var privatePath = Path.Combine(folder, "chave-privada.pem");
            if (File.Exists(privatePath))
                throw new InvalidOperationException($"Já existe {privatePath}. Trocar a chave invalida todas as licenças emitidas.");
            Directory.CreateDirectory(folder);
            var (privatePem, publicPem) = LicenseCodec.NewKeyPair();
            File.WriteAllText(privatePath, privatePem);
            File.WriteAllText(Path.Combine(folder, "chave-publica.pem"), publicPem);
            Console.WriteLine($"Chaves criadas em {folder}.\nGuarde chave-privada.pem em local seguro (com backup). Chave pública:\n\n{publicPem}");
            return 0;
        }
        case "emitir":
        {
            var customer = Required(options, "cliente");
            var machine = Required(options, "maquina");
            if (LicenseRules.Normalize(machine).Length != 16) throw new ArgumentException("Código da máquina inválido (XXXX-XXXX-XXXX-XXXX).");
            var terminals = int.Parse(Required(options, "caixas"), CultureInfo.InvariantCulture);
            if (terminals is < 1 or > 99) throw new ArgumentException("Número de caixas inválido (1 a 99).");
            var cnpj = options.GetValueOrDefault("cnpj") is { } c ? TaxDocuments.Normalize(c) : null;
            if (cnpj is not null && !TaxDocuments.IsValidCnpj(cnpj)) throw new ArgumentException("CNPJ inválido.");
            DateOnly? expires = options.GetValueOrDefault("validade") is { } v ? DateOnly.ParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            var keyPath = options.GetValueOrDefault("chave") ?? Path.Combine(defaultFolder, "chave-privada.pem");
            var data = new LicenseData($"LIC-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}", customer, cnpj,
                options.GetValueOrDefault("plano") ?? (terminals == 1 ? "Loja" : $"Loja {terminals} caixas"), terminals,
                DateOnly.FromDateTime(DateTime.Today), expires, machine.Trim().ToUpperInvariant());
            var text = LicenseCodec.Sign(data, File.ReadAllText(keyPath));
            var output = options.GetValueOrDefault("saida") ?? $"{data.Id}.pdvlic";
            File.WriteAllText(output, text);
            Console.WriteLine($"Licença {data.Id} emitida: {customer}, {data.Plan}, {terminals} caixa(s), " +
                $"{(expires is null ? "perpétua" : $"válida até {expires:dd/MM/yyyy}")}, máquina {data.MachineCode}.\nArquivo: {Path.GetFullPath(output)}");
            return 0;
        }
        case "conferir":
        {
            var file = args.Length > 1 ? args[1] : throw new ArgumentException("Informe o arquivo .pdvlic.");
            var publicPem = options.GetValueOrDefault("publica") is { } p ? File.ReadAllText(p) : LicenseKeys.Production;
            var data = LicenseCodec.Verify(File.ReadAllText(file), publicPem);
            Console.WriteLine($"Assinatura válida.\n{data}");
            return 0;
        }
        default:
            Console.Error.WriteLine($"Comando desconhecido: {args[0]} (use --help).");
            return 1;
    }
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or FormatException or Pdv.Domain.Common.DomainException)
{
    Console.Error.WriteLine($"Erro: {ex.Message}");
    return 2;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
        if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) result[args[i][2..]] = args[++i];
    return result;
}

static string Required(Dictionary<string, string> options, string name) =>
    options.GetValueOrDefault(name) ?? throw new ArgumentException($"Informe --{name}.");
