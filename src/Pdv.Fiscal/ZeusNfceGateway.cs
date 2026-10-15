using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DFe.Classes.Entidades;
using DFe.Classes.Flags;
using DFe.Utils;
using NFe.Classes;
using NFe.Classes.Informacoes;
using NFe.Classes.Informacoes.Destinatario;
using NFe.Classes.Informacoes.Detalhe;
using NFe.Classes.Informacoes.Detalhe.Tributacao;
using NFe.Classes.Informacoes.Detalhe.Tributacao.Estadual;
using NFe.Classes.Informacoes.Detalhe.Tributacao.Estadual.Tipos;
using NFe.Classes.Informacoes.Detalhe.Tributacao.Federal;
using NFe.Classes.Informacoes.Detalhe.Tributacao.Federal.Tipos;
using NFe.Classes.Informacoes.Emitente;
using NFe.Classes.Informacoes.Identificacao;
using NFe.Classes.Informacoes.Identificacao.Tipos;
using NFe.Classes.Informacoes.Observacoes;
using NFe.Classes.Informacoes.Pagamento;
using NFe.Classes.Informacoes.Total;
using NFe.Classes.Informacoes.Transporte;
using NFe.Classes.Protocolo;
using NFe.Classes.Servicos.Tipos;
using NFe.Servicos;
using NFe.Utils;
using NFe.Utils.InformacoesSuplementares;
using NFe.Utils.NFe;
using Pdv.Application.Fiscal;
using Pdv.Domain.Common;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Sales;
using AppEnvironment = Pdv.Domain.Fiscal.FiscalEnvironment;
using NfeDocument = NFe.Classes.NFe;

namespace Pdv.Fiscal;

/// <summary>
/// NFC-e (modelo 65, leiaute 4.00) com a biblioteca DFe.NET (LGPL-2.1, usada sem modificação).
/// Tributação suportada nesta versão: CSOSN 102/103/300/400/500 e CST 00/40/41/50/60; PIS/COFINS por alíquota,
/// não tributado ou "outros". Demais códigos são recusados com mensagem clara antes do envio.
/// </summary>
public sealed class ZeusNfceGateway : INfceGateway
{
    public const string HomologationText = "NOTA FISCAL EMITIDA EM AMBIENTE DE HOMOLOGACAO - SEM VALOR FISCAL";

    private static readonly int[] DeniedCodes = [110, 205, 301, 302, 303];
    private static readonly int[] UnavailableCodes = [108, 109];

    public SignedNfce BuildAndSign(NfceDraft draft, NfceSettings settings)
    {
        using var certificate = LoadCertificate(settings);
        var config = CreateConfig(settings, draft.Emitter.Uf, draft.EmissionType);

        var nfe = new NfeDocument { infNFe = BuildInfNfe(draft) };
        nfe = nfe.Assina(config, certificate);

        nfe.infNFeSupl = new infNFeSupl();
        nfe.infNFeSupl.qrCode = nfe.infNFeSupl.ObterUrlQrCode(nfe, VersaoQrCode.QrCodeVersao3, settings.CscId, settings.CscToken,
            config.Certificado);
        nfe.infNFeSupl.urlChave = nfe.infNFeSupl.ObterUrlConsulta(nfe, VersaoQrCode.QrCodeVersao3);

        return new SignedNfce(nfe.ObterXmlString(), nfe.infNFeSupl.qrCode, nfe.infNFeSupl.urlChave);
    }

    public Task<SefazResult> AuthorizeAsync(string signedXml, NfceSettings settings, string uf, CancellationToken ct) =>
        CallAsync(settings, uf, (service, _) =>
        {
            var nfe = new NfeDocument().CarregarDeXmlString(signedXml, false);
            var response = service.NFeAutorizacao((int)(nfe.infNFe.ide.nNF % int.MaxValue), IndicadorSincronizacao.Sincrono, [nfe], false);
            var batch = response.Retorno;
            if (batch.protNFe?.infProt is { } prot) return FromProtocol(prot, nfe);
            return FromStatus(batch.cStat, batch.xMotivo);
        }, ct);

    public Task<SefazResult> QueryAsync(string accessKey, NfceSettings settings, string uf, CancellationToken ct) =>
        CallAsync(settings, uf, (service, _) =>
        {
            var response = service.NfeConsultaProtocolo(accessKey).Retorno;
            if (response.cStat == 217) return new SefazResult(SefazOutcome.NotFound, response.cStat, response.xMotivo);
            if (response.protNFe?.infProt is { } prot && prot.cStat is 100 or 150)
                return new SefazResult(SefazOutcome.Authorized, prot.cStat, prot.xMotivo, prot.nProt, null, prot.dhRecbto);
            return FromStatus(response.cStat, response.xMotivo);
        }, ct);

    public Task<SefazResult> CancelAsync(string accessKey, string protocol, string cnpj, string justification, NfceSettings settings,
        string uf, CancellationToken ct) =>
        CallAsync(settings, uf, (service, _) =>
        {
            var response = service.RecepcaoEventoCancelamento(1, 1, protocol, accessKey, justification, cnpj, DateTimeOffset.Now).Retorno;
            var info = response.retEvento?.FirstOrDefault()?.infEvento;
            if (info is not null && info.cStat is 135 or 155)
                return new SefazResult(SefazOutcome.Authorized, info.cStat, info.xMotivo, info.nProt);
            return FromStatus(info?.cStat ?? response.cStat, info?.xMotivo ?? response.xMotivo);
        }, ct);

    public Task<SefazResult> ServiceStatusAsync(NfceSettings settings, string uf, CancellationToken ct) =>
        CallAsync(settings, uf, (service, _) =>
        {
            var response = service.NfeStatusServico().Retorno;
            return response.cStat == 107
                ? new SefazResult(SefazOutcome.Authorized, response.cStat, response.xMotivo)
                : FromStatus(response.cStat, response.xMotivo);
        }, ct);

    // ---------------------------------------------------------------------------------------------------

    internal static infNFe BuildInfNfe(NfceDraft d)
    {
        var homologation = d.Environment == AppEnvironment.Homologation;
        var details = d.Items.Select((item, index) => BuildItem(item, homologation && index == 0)).ToList();
        var totals = BuildTotals(details);

        var info = new infNFe
        {
            versao = "4.00",
            Id = "NFe" + d.AccessKey,
            ide = new ide
            {
                cUF = ParseUf(d.Emitter.Uf),
                cNF = d.RandomCode.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture),
                natOp = "VENDA",
                mod = ModeloDocumento.NFCe,
                serie = d.Series,
                nNF = d.Number,
                dhEmi = d.IssuedAt,
                tpNF = TipoNFe.tnSaida,
                idDest = DestinoOperacao.doInterna,
                cMunFG = long.Parse(d.Emitter.CityIbgeCode, System.Globalization.CultureInfo.InvariantCulture),
                tpImp = TipoImpressao.tiNFCe,
                tpEmis = d.EmissionType == FiscalEmissionType.OfflineContingency ? TipoEmissao.teOffLine : TipoEmissao.teNormal,
                cDV = d.AccessKey[^1] - '0',
                tpAmb = homologation ? TipoAmbiente.Homologacao : TipoAmbiente.Producao,
                finNFe = FinalidadeNFe.fnNormal,
                indFinal = ConsumidorFinal.cfConsumidorFinal,
                indPres = PresencaComprador.pcPresencial,
                procEmi = ProcessoEmissao.peAplicativoContribuinte,
                verProc = "SistemaPDV 0.1",
            },
            emit = BuildEmitter(d.Emitter),
            dest = BuildCustomer(d, homologation),
            det = details,
            total = new total { ICMSTot = totals },
            transp = new transp { modFrete = ModalidadeFrete.mfSemFrete },
            pag = [BuildPayment(d)],
            infAdic = string.IsNullOrWhiteSpace(d.AdditionalInfo) ? null : new infAdic { infCpl = d.AdditionalInfo },
        };

        if (d.EmissionType == FiscalEmissionType.OfflineContingency)
        {
            info.ide.dhCont = d.IssuedAt;
            info.ide.xJust = Truncate(Pad(d.ContingencyReason ?? "SEFAZ indisponivel no momento da venda", 15), 256);
        }
        return info;
    }

    private static emit BuildEmitter(NfceEmitter e) => new()
    {
        CNPJ = e.Cnpj,
        xNome = Truncate(e.LegalName, 60),
        xFant = string.IsNullOrWhiteSpace(e.TradeName) ? null : Truncate(e.TradeName, 60),
        IE = e.StateRegistration,
        CRT = e.TaxRegime switch
        {
            TaxRegime.SimplesNacional => CRT.SimplesNacional,
            TaxRegime.SimplesNacionalExcessoSublimite => CRT.SimplesNacionalExcessoSublimite,
            TaxRegime.SimplesNacionalMei => CRT.SimplesNacionalMei,
            _ => CRT.RegimeNormal,
        },
        enderEmit = new enderEmit
        {
            xLgr = Truncate(e.Street, 60),
            nro = Truncate(e.Number, 60),
            xCpl = string.IsNullOrWhiteSpace(e.Complement) ? null : Truncate(e.Complement, 60),
            xBairro = Truncate(e.District, 60),
            cMun = long.Parse(e.CityIbgeCode, System.Globalization.CultureInfo.InvariantCulture),
            xMun = Truncate(e.City, 60),
            UF = ParseUf(e.Uf),
            CEP = e.ZipCode,
            cPais = 1058,
            xPais = "BRASIL",
            fone = long.TryParse(e.Phone, out var phone) ? phone : null,
        },
    };

    private static dest? BuildCustomer(NfceDraft d, bool homologation)
    {
        if (d.CustomerDocument is null) return null;
        var customer = new dest(VersaoServico.Versao400)
        {
            indIEDest = indIEDest.NaoContribuinte,
            xNome = homologation ? HomologationText : (string.IsNullOrWhiteSpace(d.CustomerName) ? null : Truncate(d.CustomerName, 60)),
        };
        if (d.CustomerDocument.Length == 11) customer.CPF = d.CustomerDocument;
        else customer.CNPJ = d.CustomerDocument;
        return customer;
    }

    private static det BuildItem(NfceItem i, bool homologationFirstItem)
    {
        var discount = i.Discount > 0 ? i.Discount : (decimal?)null;
        var taxableBase = i.GrossTotal - i.Discount;
        return new det
        {
            nItem = i.LineNumber,
            prod = new prod
            {
                cProd = Truncate(i.ProductCode, 60),
                cEAN = i.Gtin ?? "SEM GTIN",
                cEANTrib = i.Gtin ?? "SEM GTIN",
                // Regra da SEFAZ para homologação: o 1º item leva o aviso de "sem valor fiscal".
                xProd = homologationFirstItem ? HomologationText : Truncate(i.Description, 120),
                NCM = i.Ncm,
                CEST = i.Cest,
                CFOP = int.Parse(i.Cfop, System.Globalization.CultureInfo.InvariantCulture),
                uCom = Truncate(i.Unit, 6),
                qCom = i.Quantity,
                vUnCom = i.UnitPrice,
                vProd = i.GrossTotal,
                uTrib = Truncate(i.Unit, 6),
                qTrib = i.Quantity,
                vUnTrib = i.UnitPrice,
                vDesc = discount,
                indTot = IndicadorTotal.ValorDoItemCompoeTotalNF,
            },
            imposto = new imposto
            {
                ICMS = new ICMS { TipoICMS = BuildIcms(i, taxableBase) },
                PIS = new PIS { TipoPIS = BuildPis(i.PisCst, i.PisRate, taxableBase) },
                COFINS = new COFINS { TipoCOFINS = BuildCofins(i.CofinsCst, i.CofinsRate, taxableBase) },
            },
        };
    }

    private static ICMSBasico BuildIcms(NfceItem i, decimal taxableBase)
    {
        var origin = (OrigemMercadoria)(int)i.Origin;
        if (i.Csosn is { } csosn)
        {
            return csosn switch
            {
                "102" => new ICMSSN102 { orig = origin, CSOSN = Csosnicms.Csosn102 },
                "103" => new ICMSSN102 { orig = origin, CSOSN = Csosnicms.Csosn103 },
                "300" => new ICMSSN102 { orig = origin, CSOSN = Csosnicms.Csosn300 },
                "400" => new ICMSSN102 { orig = origin, CSOSN = Csosnicms.Csosn400 },
                "500" => new ICMSSN500 { orig = origin, CSOSN = Csosnicms.Csosn500 },
                _ => throw new DomainException($"CSOSN {csosn} ainda não é suportado na emissão de NFC-e ({i.Description})."),
            };
        }

        return i.IcmsCst switch
        {
            "00" => new ICMS00
            {
                orig = origin, CST = Csticms.Cst00, modBC = DeterminacaoBaseIcms.DbiValorOperacao,
                vBC = taxableBase, pICMS = i.IcmsRate, vICMS = Money.Round(taxableBase * i.IcmsRate / 100),
            },
            "40" => new ICMS40 { orig = origin, CST = Csticms.Cst40 },
            "41" => new ICMS40 { orig = origin, CST = Csticms.Cst41 },
            "50" => new ICMS40 { orig = origin, CST = Csticms.Cst50 },
            "60" => new ICMS60 { orig = origin, CST = Csticms.Cst60 },
            _ => throw new DomainException($"CST {i.IcmsCst} ainda não é suportado na emissão de NFC-e ({i.Description})."),
        };
    }

    private static PISBasico BuildPis(string cst, decimal rate, decimal taxableBase)
    {
        var code = Enum.Parse<CSTPIS>("pis" + cst);
        return cst switch
        {
            "01" or "02" => new PISAliq { CST = code, vBC = taxableBase, pPIS = rate, vPIS = Money.Round(taxableBase * rate / 100) },
            "04" or "05" or "06" or "07" or "08" or "09" => new PISNT { CST = code },
            _ => new PISOutr { CST = code, vBC = 0, pPIS = 0, vPIS = 0 },
        };
    }

    private static COFINSBasico BuildCofins(string cst, decimal rate, decimal taxableBase)
    {
        var code = Enum.Parse<CSTCOFINS>("cofins" + cst);
        return cst switch
        {
            "01" or "02" => new COFINSAliq { CST = code, vBC = taxableBase, pCOFINS = rate, vCOFINS = Money.Round(taxableBase * rate / 100) },
            "04" or "05" or "06" or "07" or "08" or "09" => new COFINSNT { CST = code },
            _ => new COFINSOutr { CST = code, vBC = 0, pCOFINS = 0, vCOFINS = 0 },
        };
    }

    private static ICMSTot BuildTotals(List<det> details)
    {
        decimal vProd = 0, vDesc = 0, vBC = 0, vICMS = 0, vPIS = 0, vCOFINS = 0;
        foreach (var d in details)
        {
            vProd += d.prod.vProd;
            vDesc += d.prod.vDesc ?? 0;
            if (d.imposto.ICMS.TipoICMS is ICMS00 icms) { vBC += icms.vBC; vICMS += icms.vICMS; }
            if (d.imposto.PIS.TipoPIS is PISAliq pis) vPIS += pis.vPIS;
            if (d.imposto.COFINS.TipoCOFINS is COFINSAliq cofins) vCOFINS += cofins.vCOFINS;
        }
        return new ICMSTot
        {
            vBC = vBC, vICMS = vICMS, vICMSDeson = 0, vFCP = 0, vBCST = 0, vST = 0, vFCPST = 0, vFCPSTRet = 0,
            vProd = vProd, vFrete = 0, vSeg = 0, vDesc = vDesc, vII = 0, vIPI = 0, vIPIDevol = 0,
            vPIS = vPIS, vCOFINS = vCOFINS, vOutro = 0, vNF = vProd - vDesc,
        };
    }

    private static pag BuildPayment(NfceDraft d) => new()
    {
        detPag = d.Payments.Select(p => new detPag
        {
            tPag = p.Method switch
            {
                PaymentMethod.Cash => FormaPagamento.fpDinheiro,
                PaymentMethod.CreditCard => FormaPagamento.fpCartaoCredito,
                PaymentMethod.DebitCard => FormaPagamento.fpCartaoDebito,
                // Pix com QR dinâmico confirmado pelo PSP (17) ou sem integração, por chave/QR estático (20).
                PaymentMethod.Pix => p.Integrated ? FormaPagamento.fpPagamentoInstantaneoPIXDinamico : FormaPagamento.fpPagamentoInstantaneoPIXEstatico,
                // Vale-troca da própria loja.
                PaymentMethod.ExchangeVoucher => FormaPagamento.fpCreditoEmLoja,
                _ => FormaPagamento.fpOutro,
            },
            xPag = p.Method == PaymentMethod.Other ? "Outros" : null,
            vPag = p.Amount,
            card = BuildCard(p),
        }).ToList(),
        vTroco = d.Change > 0 ? d.Change : null,
    };

    /// <summary>
    /// Grupo do cartão/Pix: integrado (TEF ou Pix dinâmico) informa credenciadora/PSP, bandeira e autorização;
    /// maquininha não integrada ao PDV só informa tpIntegra = 2.
    /// </summary>
    private static card? BuildCard(NfcePayment p)
    {
        var isCard = p.Method is PaymentMethod.CreditCard or PaymentMethod.DebitCard;
        if (!isCard && !(p.Method == PaymentMethod.Pix && p.Integrated)) return null;
        if (!p.Integrated) return new card { tpIntegra = TipoIntegracaoPagamento.TipNaoIntegrado };
        return new card
        {
            tpIntegra = TipoIntegracaoPagamento.TipIntegradoAutomacao,
            CNPJ = string.IsNullOrWhiteSpace(p.AcquirerCnpj) ? null : p.AcquirerCnpj,
            tBand = isCard ? Brand(p.CardBrand) : null,
            cAut = isCard && !string.IsNullOrWhiteSpace(p.AuthorizationCode) ? p.AuthorizationCode : null,
        };
    }

    /// <summary>Bandeiras com nome na biblioteca; as demais (Elo, Hipercard...) saem como 99 - Outros.</summary>
    private static BandeiraCartao? Brand(string? code) => code switch
    {
        null or "" => null,
        "01" => BandeiraCartao.bcVisa,
        "02" => BandeiraCartao.bcMasterCard,
        "03" => BandeiraCartao.bcAmericanExpress,
        "04" => BandeiraCartao.bcSorocred,
        "05" => BandeiraCartao.bcDinersClub,
        _ => BandeiraCartao.bcOutros,
    };

    private static X509Certificate2 LoadCertificate(NfceSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CertificatePath) || !File.Exists(settings.CertificatePath))
            throw new DomainException("Arquivo do certificado digital (.pfx) não encontrado.");
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword,
                X509KeyStorageFlags.Exportable);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new DomainException("Não foi possível abrir o certificado: confira a senha.");
        }
    }

    private static ConfiguracaoServico CreateConfig(NfceSettings settings, string uf, FiscalEmissionType type) => new()
    {
        cUF = ParseUf(uf),
        tpAmb = settings.Environment == AppEnvironment.Homologation ? TipoAmbiente.Homologacao : TipoAmbiente.Producao,
        tpEmis = type == FiscalEmissionType.OfflineContingency ? TipoEmissao.teOffLine : TipoEmissao.teNormal,
        ModeloDocumento = ModeloDocumento.NFCe,
        DefineVersaoServicosAutomaticamente = true,
        VersaoLayout = VersaoServico.Versao400,
        TimeOut = settings.TimeoutSeconds * 1000,
        ProtocoloDeSeguranca = SecurityProtocolType.Tls12,
        ValidarSchemas = false,
        RemoverAcentos = false,
        Certificado = new ConfiguracaoCertificado
        {
            TipoCertificado = TipoCertificado.A1Arquivo,
            Arquivo = settings.CertificatePath,
            Senha = settings.CertificatePassword,
        },
    };

    private static Task<SefazResult> CallAsync(NfceSettings settings, string uf, Func<ServicosNFe, ConfiguracaoServico, SefazResult> call,
        CancellationToken ct) =>
        Task.Run(() =>
        {
            try
            {
                using var certificate = LoadCertificate(settings);
                var config = CreateConfig(settings, uf, FiscalEmissionType.Normal);
                using var service = new ServicosNFe(config, certificate);
                return call(service, config);
            }
            catch (DomainException)
            {
                throw;
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                // Não conectou: a nota não saiu do computador, pode emitir em contingência.
                return new SefazResult(SefazOutcome.Unavailable, 0, $"Sem conexão com a SEFAZ: {Innermost(ex).Message}");
            }
            catch (Exception ex)
            {
                // Pode ter chegado à SEFAZ: a próxima tentativa consulta pela chave antes de reenviar.
                return new SefazResult(SefazOutcome.Unknown, 0, $"Sem resposta conclusiva da SEFAZ: {Innermost(ex).Message}");
            }
        }, ct);

    private static bool IsConnectionFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException) return true;
            if (e is WebException { Status: WebExceptionStatus.ConnectFailure or WebExceptionStatus.NameResolutionFailure }) return true;
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }) return true;
        }
        return false;
    }

    private static SefazResult FromProtocol(infProt prot, NfeDocument nfe)
    {
        if (prot.cStat is 100 or 150)
        {
            var proc = new nfeProc { versao = "4.00", NFe = nfe, protNFe = new protNFe { versao = "4.00", infProt = prot } };
            return new SefazResult(SefazOutcome.Authorized, prot.cStat, prot.xMotivo, prot.nProt,
                FuncoesXml.ClasseParaXmlString(proc), prot.dhRecbto);
        }
        return FromStatus(prot.cStat, prot.xMotivo, prot.nProt);
    }

    private static SefazResult FromStatus(int code, string message, string? protocol = null) =>
        DeniedCodes.Contains(code) ? new SefazResult(SefazOutcome.Denied, code, message, protocol)
        : UnavailableCodes.Contains(code) ? new SefazResult(SefazOutcome.Unavailable, code, message)
        : new SefazResult(SefazOutcome.Rejected, code, message);

    private static Estado ParseUf(string uf) => Enum.Parse<Estado>(uf, ignoreCase: true);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Pad(string value, int min) => value.Length >= min ? value : value.PadRight(min, '.');

    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }
}
