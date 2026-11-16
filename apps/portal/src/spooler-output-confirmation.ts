const physicalOutputConfirmationStatuses = new Set([
  "SPOOLER_ACCEPTED",
  "PRINTING",
  "OUTPUT_UNCONFIRMED"
]);

export function isPhysicalOutputConfirmationStatus(status?: string | null): boolean {
  return physicalOutputConfirmationStatuses.has((status ?? "").trim().toUpperCase());
}

export function isRetainedSpoolerJob(status?: string | null): boolean {
  return (status ?? "").trim().toUpperCase() === "RETAINED";
}

export function canConfirmPhysicalOutput(jobStatus?: string | null, spoolerStatus?: string | null): boolean {
  return isPhysicalOutputConfirmationStatus(jobStatus) && !isRetainedSpoolerJob(spoolerStatus);
}
