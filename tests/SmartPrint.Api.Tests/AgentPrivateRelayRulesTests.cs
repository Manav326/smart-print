using SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

namespace SmartPrint.Api.Tests;

public sealed class AgentPrivateRelayRulesTests
{
    [Fact]
    public void PdfVirtualOutputIsDisabledInProductionEvenWhenOperationalSettingIsEnabled()
    {
        Assert.False(AgentPrivateRelayRules.AllowsPdfVirtualOutput(
            isProduction: true, operationalSettingEnabled: true));
    }

    [Fact]
    public void PdfVirtualOutputRequiresBothNonProductionAndExplicitOperationalEnablement()
    {
        Assert.False(AgentPrivateRelayRules.AllowsPdfVirtualOutput(
            isProduction: false, operationalSettingEnabled: false));
        Assert.True(AgentPrivateRelayRules.AllowsPdfVirtualOutput(
            isProduction: false, operationalSettingEnabled: true));
    }
}
