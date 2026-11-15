export function isRetainedSpoolerJob(status?: string | null): boolean {
  return (status ?? "").trim().toUpperCase() === "RETAINED";
}
