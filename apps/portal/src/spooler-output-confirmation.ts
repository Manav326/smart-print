const physicalOutputConfirmationStatuses = new Set([
  "SPOOLER_ACCEPTED",
  "PRINTING",
  "OUTPUT_UNCONFIRMED"
]);

const verifiedAgentSpoolerStatuses = new Set([
  "RETAINED",
  "OFFLINE",
  "PAPEROUT",
  "PAUSED",
  "ERROR",
  "USERINTERVENTION",
  "PRINTING",
  "SPOOLING",
  "PRINTED",
  "QUEUED",
  "OUTPUT_UNCONFIRMED"
]);

const spoolerWarningStatuses = new Set([
  "OFFLINE",
  "PAPEROUT",
  "PAUSED",
  "ERROR",
  "USERINTERVENTION"
]);

const printerWarningStatuses = new Set([
  "OFFLINE",
  "PAUSED"
]);

function normalizeStatus(status?: string | null): string {
  return (status ?? "").trim().toUpperCase();
}

export function isPhysicalOutputConfirmationStatus(status?: string | null): boolean {
  return physicalOutputConfirmationStatuses.has(normalizeStatus(status));
}

export function isRetainedSpoolerJob(status?: string | null): boolean {
  return normalizeStatus(status) === "RETAINED";
}

export function hasVerifiedAgentSpoolerObservation(
  spoolerStatus?: string | null,
  spoolerObservedUtc?: string | null,
  failureCode?: string | null,
  spoolerJobId?: number | null
): boolean {
  const normalizedSpoolerStatus = normalizeStatus(spoolerStatus);
  return !!spoolerObservedUtc &&
    Number.isFinite(Date.parse(spoolerObservedUtc)) &&
    verifiedAgentSpoolerStatuses.has(normalizedSpoolerStatus) &&
    (normalizedSpoolerStatus !== "OUTPUT_UNCONFIRMED" ||
      isVerifiedSpoolerAbsenceReport(failureCode, spoolerJobId));
}

export function isVerifiedSpoolerAbsenceReport(
  reasonCode?: string | null,
  spoolerJobId?: number | null
): boolean {
  const normalizedReason = normalizeStatus(reasonCode);
  return (typeof spoolerJobId === "number" && spoolerJobId > 0 && normalizedReason === "SPOOLER_JOB_NO_LONGER_VISIBLE") ||
    (spoolerJobId == null && normalizedReason === "AGENT_RESTART_DURING_PRINT_SUBMISSION");
}

export function canConfirmPhysicalOutput(
  jobStatus?: string | null,
  spoolerStatus?: string | null,
  spoolerObservedUtc?: string | null,
  printerStatus?: string | null,
  failureCode?: string | null,
  spoolerJobId?: number | null
): boolean {
  const normalizedJobStatus = normalizeStatus(jobStatus);
  const normalizedSpoolerStatus = normalizeStatus(spoolerStatus);

  if (!isPhysicalOutputConfirmationStatus(normalizedJobStatus) ||
      !hasVerifiedAgentSpoolerObservation(normalizedSpoolerStatus, spoolerObservedUtc, failureCode, spoolerJobId) ||
      normalizedSpoolerStatus === "RETAINED") {
    return false;
  }

  // OUTPUT_UNCONFIRMED is the deliberate manual-resolution path: the shop
  // checks the actual pages even when Windows reported a queue warning.
  if (normalizedJobStatus === "OUTPUT_UNCONFIRMED") return true;

  return !spoolerWarningStatuses.has(normalizedSpoolerStatus) &&
    !printerWarningStatuses.has(normalizeStatus(printerStatus));
}
