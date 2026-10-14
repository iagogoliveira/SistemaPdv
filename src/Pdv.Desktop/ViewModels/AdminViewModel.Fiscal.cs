using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pdv.Application;
using Pdv.Application.Fiscal;
using Pdv.Desktop.Views;
using Pdv.Domain.Fiscal;
using Pdv.Domain.Identity;

namespace Pdv.Desktop.ViewModels;

/// <summary>Retaguarda — abas Empresa (emitente) e Tributação (perfis tributários).</summary>
public partial class AdminViewModel
{
    public bool CanManageCompany => state.User!.Can(Permissions.ManageCompany);

    public CompanyForm Company { get; } = new();

    public ObservableCollection<TaxProfileModel> TaxProfiles { get; } = [];

    [ObservableProperty]
    private TaxProfileModel? _selectedTaxProfile;

    [ObservableProperty]
    private string _fiscalStatus = "";

    [ObservableProperty]
    private bool _isFiscalReady;

    private async Task LoadFiscalAsync()
    {
        if (CanManageCompany)
        {
            var model = await Runner.RunAsync<CompanySettingsService, CompanySettingsModel>(s => s.GetAsync());
            await LoadTaxProfilesAsync();
            Company.Load(model, TaxProfiles);
        }
        await RefreshReadinessAsync();
        await LoadNfceAsync();
    }

    private async Task LoadTaxProfilesAsync()
    {
        var list = await Runner.RunAsync<TaxProfileService, IReadOnlyList<TaxProfileModel>>(s => s.ListAsync(includeInactive: true));
        var selected = SelectedTaxProfile?.Id;
        TaxProfiles.Clear();
        foreach (var p in list) TaxProfiles.Add(p);
        SelectedTaxProfile = TaxProfiles.FirstOrDefault(p => p.Id == selected);
        Company.RefreshProfiles(TaxProfiles);
    }

    private async Task RefreshReadinessAsync()
    {
        var r = await Runner.RunAsync<CompanySettingsService, FiscalReadiness>(s => s.GetReadinessAsync());
        IsFiscalReady = r.IsReady;
        FiscalStatus = (r.CompanyConfigured, r.ProductsWithoutNcm) switch
        {
            (true, 0) => "Cadastro fiscal completo: empresa configurada e todos os produtos ativos com NCM.",
            (false, 0) => "Complete os dados da empresa (aba Empresa) para emitir NFC-e.",
            (true, var n) => $"{n} produto(s) ativo(s) sem NCM. Eles não poderão sair em NFC-e até serem completados.",
            (false, var n) => $"Complete os dados da empresa e o NCM de {n} produto(s) para emitir NFC-e.",
        };
    }

    [RelayCommand]
    private async Task SaveCompanyAsync()
    {
        if (!CanManageCompany) { Dialogs.ShowError("Seu usuário não tem permissão para alterar os dados da empresa."); return; }
        var result = await Runner.RunAsync<CompanySettingsService, Result<Guid>>(s => s.SaveAsync(ActorId, Company.ToModel()));
        if (!result.Succeeded) { Dialogs.ShowError(result.Error!); return; }
        Dialogs.ShowInfo("Dados da empresa salvos.");
        await LoadFiscalAsync();
    }

    [RelayCommand]
    private Task NewTaxProfileAsync() => EditTaxProfileAsync(null);

    [RelayCommand]
    private Task EditSelectedTaxProfileAsync() => SelectedTaxProfile is null ? Task.CompletedTask : EditTaxProfileAsync(SelectedTaxProfile);

    private async Task EditTaxProfileAsync(TaxProfileModel? existing)
    {
        if (!CanManageCompany) { Dialogs.ShowError("Seu usuário não tem permissão para alterar a tributação."); return; }
        var form = new TaxProfileForm(Company.Regime?.Value ?? TaxRegime.SimplesNacional, existing);
        while (true)
        {
            var dialog = new TaxProfileEditDialog(form) { Owner = System.Windows.Application.Current.Windows.OfType<AdminWindow>().FirstOrDefault() };
            if (dialog.ShowDialog() != true) return;
            if (!form.TryBuild(out var model, out var error)) { Dialogs.ShowError(error!); continue; }

            var result = await Runner.RunAsync<TaxProfileService, Result<Guid>>(s => s.SaveAsync(ActorId, model!));
            if (!result.Succeeded) { Dialogs.ShowError(result.Error!); continue; }
            break;
        }
        await LoadTaxProfilesAsync();
    }

    [RelayCommand]
    private async Task ToggleTaxProfileActiveAsync()
    {
        if (SelectedTaxProfile is not { Id: { } id } p) return;
        var result = await Runner.RunAsync<TaxProfileService, Result<Guid>>(s => s.SetActiveAsync(ActorId, id, !p.IsActive));
        if (!result.Succeeded) Dialogs.ShowError(result.Error!);
        await LoadTaxProfilesAsync();
    }
}

public partial class CompanyForm : ObservableObject
{
    public IReadOnlyList<Choice<TaxRegime>> Regimes { get; } = FiscalLabels.Regimes;
    public IReadOnlyList<string> Ufs { get; } = FiscalLabels.Ufs;
    public ObservableCollection<Choice<Guid?>> Profiles { get; } = [];

    [ObservableProperty] private string _legalName = "";
    [ObservableProperty] private string _tradeName = "";
    [ObservableProperty] private string _cnpj = "";
    [ObservableProperty] private string _stateRegistration = "";
    [ObservableProperty] private string _municipalRegistration = "";
    [ObservableProperty] private Choice<TaxRegime>? _regime;
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _zipCode = "";
    [ObservableProperty] private string _street = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _complement = "";
    [ObservableProperty] private string _district = "";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _cityIbgeCode = "";
    [ObservableProperty] private string? _uf;
    [ObservableProperty] private string _storeName = "";
    [ObservableProperty] private Choice<Guid?>? _defaultProfile;

    public void Load(CompanySettingsModel m, IEnumerable<TaxProfileModel> profiles)
    {
        LegalName = m.LegalName;
        TradeName = m.TradeName ?? "";
        Cnpj = m.Cnpj is null ? "" : TaxDocuments.Format(m.Cnpj);
        StateRegistration = m.StateRegistration ?? "";
        MunicipalRegistration = m.MunicipalRegistration ?? "";
        Regime = Regimes.First(r => r.Value == m.TaxRegime);
        Phone = m.Phone ?? "";
        ZipCode = m.ZipCode is { Length: 8 } z ? $"{z[..5]}-{z[5..]}" : m.ZipCode ?? "";
        Street = m.Street ?? "";
        Number = m.Number ?? "";
        Complement = m.Complement ?? "";
        District = m.District ?? "";
        City = m.City ?? "";
        CityIbgeCode = m.CityIbgeCode ?? "";
        Uf = m.Uf;
        StoreName = m.StoreName;
        RefreshProfiles(profiles, m.DefaultTaxProfileId);
    }

    public void RefreshProfiles(IEnumerable<TaxProfileModel> profiles, Guid? selected = null)
    {
        selected ??= DefaultProfile?.Value ?? profiles.FirstOrDefault(p => p.IsDefault)?.Id;
        Profiles.Clear();
        foreach (var p in profiles.Where(p => p.IsActive))
            Profiles.Add(new Choice<Guid?>(p.Id, $"{p.Name}  ·  {p.IcmsCode}"));
        DefaultProfile = Profiles.FirstOrDefault(p => p.Value == selected);
    }

    public CompanySettingsModel ToModel() =>
        new(LegalName, TradeName, Cnpj, StateRegistration, MunicipalRegistration, Regime?.Value ?? TaxRegime.SimplesNacional,
            Street, Number, Complement, District, City, CityIbgeCode, Uf, ZipCode, Phone, DefaultProfile?.Value, StoreName);
}

public partial class TaxProfileForm : ObservableObject
{
    private readonly Guid? _id;

    public TaxProfileForm(TaxRegime regime, TaxProfileModel? existing)
    {
        UsesCsosn = existing?.Csosn is not null || (existing is null && regime.UsesCsosn());
        RegimeHint = UsesCsosn
            ? "Empresa do Simples Nacional: a tributação do ICMS usa CSOSN."
            : "Empresa do regime normal: a tributação do ICMS usa CST e alíquota.";
        Title = existing is null ? "Novo perfil tributário" : "Editar perfil tributário";
        IcmsCodes = UsesCsosn ? FiscalLabels.Csosn : FiscalLabels.IcmsCst;

        _id = existing?.Id;
        Name = existing?.Name ?? "";
        Cfop = existing?.Cfop ?? "5102";
        IcmsCode = IcmsCodes.FirstOrDefault(c => c.Value == (existing?.Csosn ?? existing?.IcmsCst)) ?? IcmsCodes.First(c => c.Value is "102" or "00");
        IcmsRate = Format(existing?.IcmsRate ?? 0);
        PisCst = FiscalLabels.PisCofinsCst.FirstOrDefault(c => c.Value == existing?.PisCst) ?? FiscalLabels.PisCofinsCst.First(c => c.Value == (UsesCsosn ? "49" : "01"));
        PisRate = Format(existing?.PisRate ?? 0);
        CofinsCst = FiscalLabels.PisCofinsCst.FirstOrDefault(c => c.Value == existing?.CofinsCst) ?? FiscalLabels.PisCofinsCst.First(c => c.Value == (UsesCsosn ? "49" : "01"));
        CofinsRate = Format(existing?.CofinsRate ?? 0);
    }

    public string Title { get; }
    public string RegimeHint { get; }
    public bool UsesCsosn { get; }
    public string IcmsLabel => UsesCsosn ? "CSOSN" : "CST do ICMS";
    public IReadOnlyList<Choice<string>> IcmsCodes { get; }
    public IReadOnlyList<Choice<string>> PisCofinsCodes { get; } = FiscalLabels.PisCofinsCst;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _cfop;
    [ObservableProperty] private Choice<string>? _icmsCode;
    [ObservableProperty] private string _icmsRate;
    [ObservableProperty] private Choice<string>? _pisCst;
    [ObservableProperty] private string _pisRate;
    [ObservableProperty] private Choice<string>? _cofinsCst;
    [ObservableProperty] private string _cofinsRate;

    public bool TryBuild(out TaxProfileModel? model, out string? error)
    {
        model = null;
        error = null;
        if (!TryRate(IcmsRate, out var icms) || !TryRate(PisRate, out var pis) || !TryRate(CofinsRate, out var cofins))
        {
            error = "Alíquotas inválidas (use números como 18 ou 1,65).";
            return false;
        }
        model = new TaxProfileModel(_id, Name, Cfop, UsesCsosn ? IcmsCode?.Value : null, UsesCsosn ? null : IcmsCode?.Value, icms,
            PisCst?.Value ?? "", pis, CofinsCst?.Value ?? "", cofins);
        return true;
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.CurrentCulture);

    private static bool TryRate(string text, out decimal value) =>
        CheckoutViewModel.TryParseDecimal(string.IsNullOrWhiteSpace(text) ? "0" : text.Replace("%", "", StringComparison.Ordinal), out value);
}
