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
  spoolerObservedUtc?: string | null
): boolean {
  return !!spoolerObservedUtc &&
    Number.isFinite(Date.parse(spoolerObservedUtc)) &&
    verifiedAgentSpoolerStatuses.has(normalizeStatus(spoolerStatus));
}

export function canConfirmPhysicalOutput(
  jobStatus?: string | null,
  spoolerStatus?: string | null,
  spoolerObservedUtc?: string | null,
  printerStatus?: string | null
): boolean {
  const normalizedJobStatus = normalizeStatus(jobStatus);
  const normalizedSpoolerStatus = normalizeStatus(spoolerStatus);

  if (!isPhysicalOutputConfirmationStatus(normalizedJobStatus) ||
      !hasVerifiedAgentSpoolerObservation(normalizedSpoolerStatus, spoolerObservedUtc) ||
      normalizedSpoolerStatus === "RETAINED") {
    return false;
  }

  // OUTPUT_UNCONFIRMED is the deliberate manual-resolution path: the shop
  // checks the actual pages even when Windows reported a queue warning.
  if (normalizedJobStatus === "OUTPUT_UNCONFIRMED") return true;

  return !spoolerWarningStatuses.has(normalizedSpoolerStatus) &&
    !printerWarningStatuses.has(normalizeStatus(printerStatus));
}
