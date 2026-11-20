namespace SmartPrint.Api.Modules.AgentPrivateDocuments.Domain;

/// <summary>
/// The production boundary independently disables PDF virtual output for Private Printing.
/// A stale or accidentally enabled operational setting must never override this rule.
/// </summary>
public static class AgentPrivateRelayRules
{
    public static bool AllowsPdfVirtualOutput(bool isProduction, bool operationalSettingEnabled) =>
        !isProduction && operationalSettingEnabled;
}
