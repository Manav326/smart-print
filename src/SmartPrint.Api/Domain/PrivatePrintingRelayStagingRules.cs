namespace SmartPrint.Api.Domain;

/// <summary>
/// Requirements for handing an already accepted private encrypted relay to the
/// shop Agent's protected local queue. Printer runtime readiness is intentionally
/// not included: print dispatch, not document staging, gates physical output.
/// </summary>
public static class PrivatePrintingRelayStagingRules
{
    public static bool CanStage(
        bool agentConnected,
        bool printerAssignedToAgent,
        bool printerActive,
        bool queueConfigured,
        bool privateOutputEligible) =>
        agentConnected &&
        printerAssignedToAgent &&
        printerActive &&
        queueConfigured &&
        privateOutputEligible;
}
