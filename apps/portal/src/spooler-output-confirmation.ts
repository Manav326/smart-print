const physicalOutputConfirmationStatuses = new Set([
  "SPOOLER_ACCEPTED",
  "PRINTING",
  "OUTPUT_UNCONFIRMED"
]);

const verifiedAgentSpoolerStatuses = new Set([
  "RETAINED", "OFFLINE", "PAPEROUT", "PAUSED", "ERROR", "USERINTERVENTION",
  "PRINTING", "SPOOLING", "PRINTED", "QUEUED", "OUTPUT_UNCONFIRMED"
]);
const printerWarningStatuses = new Set(["OFFLINE", "PAUSED"]);
const maximumSpoolerObservationAgeMs = 10 * 60 * 1000;

function normalizeStatus(status?: string | null): string {
  return (status ?? "").trim().toUpperCase();
}

export function isPhysicalOutputConfirmationStatus(status?: string | null): boolean {
  return physicalOutputConfirmationStatuses.has(normalizeStatus(status));
}

export function isRetainedSpoolerJob(status?: string | null): boolean {
  return normalizeStatus(status) === "RETAINED";
}

export function isFreshSpoolerObservation(
  spoolerSubmittedUtc?: string | null,
  spoolerObservedUtc?: string | null,
  nowUtcMs = Date.now()
): boolean {
  if (!spoolerSubmittedUtc || !spoolerObservedUtc) return false;
  const submitted = Date.parse(spoolerSubmittedUtc);
  const observed = Date.parse(spoolerObservedUtc);
  return Number.isFinite(submitted) && Number.isFinite(observed) &&
    observed >= submitted && observed <= nowUtcMs &&
    nowUtcMs - observed <= maximumSpoolerObservationAgeMs;
}

export function hasVerifiedAgentSpoolerObservation(
  spoolerStatus?: string | null,
  spoolerSubmittedUtc?: string | null,
  spoolerObservedUtc?: string | null,
  failureCode?: string | null,
  spoolerJobId?: number | null,
  nowUtcMs = Date.now()
): boolean {
  const normalizedSpoolerStatus = normalizeStatus(spoolerStatus);
  return isFreshSpoolerObservation(spoolerSubmittedUtc, spoolerObservedUtc, nowUtcMs) &&
    verifiedAgentSpoolerStatuses.has(normalizedSpoolerStatus) &&
    (normalizedSpoolerStatus !== "OUTPUT_UNCONFIRMED" ||
      isVerifiedSpoolerAbsenceReport(failureCode, spoolerJobId));
}

export function isVerifiedSpoolerAbsenceReport(
  reasonCode?: string | null,
  spoolerJobId?: number | null
): boolean {
  return typeof spoolerJobId === "number" && spoolerJobId > 0 &&
    normalizeStatus(reasonCode) === "SPOOLER_JOB_NO_LONGER_VISIBLE";
}

export function canManuallyReconcileUncertainOutput(
  jobStatus?: string | null,
  spoolerStatus?: string | null,
  failureCode?: string | null,
  spoolerJobId?: number | null,
  queueName?: string | null,
  spoolerJobName?: string | null
): boolean {
  return normalizeStatus(jobStatus) === "OUTPUT_UNCONFIRMED" &&
    !isRetainedSpoolerJob(spoolerStatus) &&
    normalizeStatus(spoolerStatus) !== "PRINTED" &&
    normalizeStatus(failureCode) === "AGENT_RESTART_DURING_PRINT_SUBMISSION" &&
    spoolerJobId == null && !!queueName?.trim() && !!spoolerJobName?.trim();
}

export function hasIncompleteSpoolerPageProgress(pagesPrinted?: number | null, totalPages?: number | null): boolean {
  return (totalPages ?? 0) > 0 && (pagesPrinted ?? 0) < (totalPages ?? 0);
}

export function canResumeUnprintedPhysicalJob(
  jobStatus?: string | null, outputMode?: string | null, outputType?: string | null,
  spoolerStatus?: string | null, failureCode?: string | null, spoolerJobId?: number | null,
  pagesPrinted?: number | null, totalPages?: number | null,
  spoolerSubmittedUtc?: string | null, spoolerObservedUtc?: string | null,
  outputConfirmedUtc?: string | null, dispatchLeaseExpiresUtc?: string | null,
  localQueueReceivedUtc?: string | null, originalQueueReady = false, nowUtcMs = Date.now()
): boolean {
  return normalizeStatus(jobStatus) === "OUTPUT_UNCONFIRMED" &&
    normalizeStatus(outputMode) === "PHYSICAL" &&
    normalizeStatus(outputType) === "PHYSICAL" &&
    normalizeStatus(spoolerStatus) === "OUTPUT_UNCONFIRMED" &&
    isVerifiedSpoolerAbsenceReport(failureCode, spoolerJobId) &&
    (pagesPrinted ?? 0) === 0 && (totalPages ?? 0) > 0 &&
    isFreshSpoolerObservation(spoolerSubmittedUtc, spoolerObservedUtc, nowUtcMs) &&
    !outputConfirmedUtc && !dispatchLeaseExpiresUtc && !!localQueueReceivedUtc && originalQueueReady;
}

export function canConfirmPhysicalOutput(
  jobStatus?: string | null,
  spoolerStatus?: string | null,
  spoolerSubmittedUtc?: string | null,
  spoolerObservedUtc?: string | null,
  printerStatus?: string | null,
  failureCode?: string | null,
  spoolerJobId?: number | null,
  nowUtcMs = Date.now(),
  pagesPrinted = 0,
  totalPages = 0
): boolean {
  const normalizedJobStatus = normalizeStatus(jobStatus);
  const normalizedSpoolerStatus = normalizeStatus(spoolerStatus);
  if (!isPhysicalOutputConfirmationStatus(normalizedJobStatus) ||
      !hasVerifiedAgentSpoolerObservation(
        normalizedSpoolerStatus, spoolerSubmittedUtc, spoolerObservedUtc,
        failureCode, spoolerJobId, nowUtcMs
      ) || normalizedSpoolerStatus === "RETAINED") return false;

  if (normalizedSpoolerStatus === "OUTPUT_UNCONFIRMED") {
    return normalizedJobStatus === "OUTPUT_UNCONFIRMED" &&
      isVerifiedSpoolerAbsenceReport(failureCode, spoolerJobId) &&
      !hasIncompleteSpoolerPageProgress(pagesPrinted, totalPages);
  }
  if (normalizedSpoolerStatus !== "PRINTED") return false;
  return !printerWarningStatuses.has(normalizeStatus(printerStatus));
}
