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
 * Mirrors the server's selection contract: both physical and PDF queues need a
 * fresh Agent heartbeat plus a fresh Ready/Busy Windows queue report.
 */
export function getPrinterReadinessReason(
  printer: PrinterDevice,
  agents: readonly AgentDevice[],
  now = Date.now()
): string | null {
  if (printer.outputMode === "UNSUPPORTED_VIRTUAL") return "Unsupported Windows output queue";
  if (!printer.agentDeviceId) return "Waiting for Print Agent discovery";

  const agent = agents.find(candidate => candidate.id === printer.agentDeviceId);
  if (!agent || !isAgentOnlineForShop(agent, now)) return "Shop computer offline";

  if (printer.outputMode !== "PHYSICAL" && printer.outputMode !== "PDF_EXPORT")
    return "Unsupported Windows output queue";
  if (!hasRecentHeartbeat(printer.lastSeenUtc, now)) return "Windows printer queue has not reported recently";
  const queueStatus = printer.status.toLowerCase();
  if (queueStatus === "ready" || queueStatus === "busy") return null;
  return "Windows queue reports " + queueStatus;
}

export function getPrinterAvailabilityReason(
  printer: PrinterDevice,
  agents: readonly AgentDevice[],
  now = Date.now()
): string | null {
  if (!printer.isActive) return "Not selected for new jobs";
  return getPrinterReadinessReason(printer, agents, now);
}

export function isPrinterAvailableForShop(
  printer: PrinterDevice,
  agents: readonly AgentDevice[],
  now = Date.now()
): boolean {
  return getPrinterAvailabilityReason(printer, agents, now) === null;
}
