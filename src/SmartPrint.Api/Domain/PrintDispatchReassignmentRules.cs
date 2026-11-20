namespace SmartPrint.Api.Domain;

/// <summary>
/// A job is safe to reassign after the shop authorised printing only while
/// it is still waiting for the Agent to claim it. Once the Agent starts a
/// submission lease or Windows provides any spooler evidence, the job must
/// remain locked to its original output path until it is reconciled.
/// </summary>
public static class PrintDispatchReassignmentRules
{
    public static bool CanReassignBeforeSpoolerSubmission(
        string? status,
        string? spoolerStatus,
        int? spoolerJobId,
        DateTimeOffset? printDispatchLeaseExpiresUtc,
        DateTimeOffset? spoolerSubmittedUtc,
        DateTimeOffset? spoolerObservedUtc,
        DateTimeOffset? outputConfirmedUtc)
    {
        return string.Equals(status, "PRINT_AUTHORISED", StringComparison.Ordinal) &&
               string.Equals(spoolerStatus, "AWAITING_AGENT", StringComparison.Ordinal) &&
               spoolerJobId is null &&
               printDispatchLeaseExpiresUtc is null &&
               spoolerSubmittedUtc is null &&
               spoolerObservedUtc is null &&
               outputConfirmedUtc is null;
    }
}
