using Pdv.Application;
using Pdv.Application.Maintenance;

namespace Pdv.Server.Tests;

/// <summary>Assistente de implantação: checklist montado pelo servidor e acessado pelo retaguarda de um caixa.</summary>
[Collection(nameof(ServerGroup))]
public class SetupAssistantTests(ServerFixture fx)
{
    [Fact]
    public async Task Checklist_reflects_the_real_state_and_records_confirmations()
    {
        var t = await fx.PairAsync("CAIXA-IMPLANTACAO");
        var checklist = await t.Runner.RunAsync<ISetupAssistant, SetupChecklist>(s => s.GetAsync(fx.AdminUserId, default));

        Assert.Equal(["empresa", "tributacao", "produtos", "ibpt", "certificado", "csc", "nfce", "sefaz", "usuarios", "caixas", "licenca", "backup"],
            checklist.Items.Select(i => i.Key));
        Assert.False(checklist.Complete);
        Assert.Equal(SetupState.Ok, Item(checklist, "caixas").State);
        Assert.Equal(SetupState.Pending, Item(checklist, "certificado").State);
        Assert.Equal(SetupState.Pending, Item(checklist, "ibpt").State);
        Assert.Equal(checklist.Items.Count, checklist.Ready + checklist.Items.Count(i => i.State == SetupState.Warning) + checklist.Pending);

        // Sem certificado/CSC o teste da SEFAZ nem tenta sair do servidor.
        var test = await t.Runner.RunAsync<ISetupAssistant, Result<string>>(s => s.TestSefazAsync(fx.AdminUserId, default));
        Assert.Contains("certificado", test.Error);

        var before = Item(checklist, "tributacao").State;
        var confirmed = await t.Runner.RunAsync<ISetupAssistant, SetupChecklist>(s => s.ConfirmTaxReviewAsync(fx.AdminUserId, default));
        // Com perfil padrão escolhido, a conferência do contador marca o item como pronto.
        if (before == SetupState.Warning) Assert.Equal(SetupState.Ok, Item(confirmed, "tributacao").State);

        // Usuário sem permissão (ou inexistente) não vê o checklist.
        await Assert.ThrowsAnyAsync<Exception>(() => t.Runner.RunAsync<ISetupAssistant, SetupChecklist>(s => s.GetAsync(Guid.NewGuid(), default)));
    }

    private static SetupItem Item(SetupChecklist checklist, string key) => checklist.Items.Single(i => i.Key == key);
}
