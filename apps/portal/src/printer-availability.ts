import type { AgentDevice, PrinterDevice } from "./api";

const DEVICE_FRESHNESS_MS = 120_000;

function hasRecentHeartbeat(value: string | null | undefined, now: number): boolean {
  if (!value) return false;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) && timestamp >= now - DEVICE_FRESHNESS_MS;
}

export function isAgentOnlineForShop(agent: AgentDevice, now = Date.now()): boolean {
  return !agent.revokedUtc &&
    agent.status === "Online" &&
    hasRecentHeartbeat(agent.lastSeenUtc, now);
}

/**
 * Mirrors assignment eligibility: a physical queue needs a recent Windows
 * heartbeat and Ready/Busy state; PDF export only needs its live paired Agent.
 */
export function getPrinterAvailabilityReason(
  printer: PrinterDevice,
  agents: readonly AgentDevice[],
  now = Date.now()
): string | null {
  if (!printer.isActive) return "Not selected for new jobs";
  if (printer.outputMode === "UNSUPPORTED_VIRTUAL") return "Unsupported Windows output queue";
  if (!printer.agentDeviceId) return "Waiting for Print Agent discovery";

  const agent = agents.find(candidate => candidate.id === printer.agentDeviceId);
  if (!agent || !isAgentOnlineForShop(agent, now)) return "Shop computer offline";

  if (printer.outputMode === "PDF_EXPORT") return null;
  if (printer.outputMode !== "PHYSICAL") return "Unsupported Windows output queue";
  if (!hasRecentHeartbeat(printer.lastSeenUtc, now)) return "Windows printer queue has not reported recently";
  if (printer.status === "Ready" || printer.status === "Busy") return null;
  return "Windows queue reports " + printer.status.toLowerCase();
}

export function isPrinterAvailableForShop(
  printer: PrinterDevice,
  agents: readonly AgentDevice[],
  now = Date.now()
): boolean {
  return getPrinterAvailabilityReason(printer, agents, now) === null;
}
