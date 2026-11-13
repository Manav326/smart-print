export interface CustomerBatchStatusItem {
  job: { id: string; status?: string | null };
}

export type CustomerBatchStatusLookup = Readonly<Record<
  string, { status?: string | null } | null | undefined
>>;

const terminalCustomerJobStatuses = new Set([
  "COMPLETED", "PDF_EXPORTED", "TEST_OUTPUT_READY", "REJECTED", "REFUNDED", "CANCELLED", "FAILED"
]);

export function isCustomerJobTerminalStatus(status: string | null | undefined): boolean {
  return typeof status === "string" && terminalCustomerJobStatuses.has(status.trim().toUpperCase());
}

/**
 * Offer a fresh QR session only after every uploaded file in the batch is terminal.
 * The selected/primary file cannot gate a finished mixed batch by itself.
 */
export function isCustomerBatchFinished(
  items: readonly CustomerBatchStatusItem[], statuses: CustomerBatchStatusLookup,
  selectedJobId?: string | null, selectedStatus?: string | null
): boolean {
  if (items.length === 0) return isCustomerJobTerminalStatus(selectedStatus);
  return items.every(({ job }) => {
    const currentStatus = job.id === selectedJobId && selectedStatus
      ? selectedStatus : statuses[job.id]?.status ?? job.status;
    return isCustomerJobTerminalStatus(currentStatus);
  });
}
