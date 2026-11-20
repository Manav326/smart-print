import assert from "node:assert/strict";
import test from "node:test";
import type { AgentDevice, PrinterDevice } from "../src/api.ts";
import { getPrinterAvailabilityReason, isPrinterAvailableForShop } from "../src/printer-availability.ts";

const now = Date.parse("2026-11-20T12:00:00Z");
const recent = new Date(now - 15_000).toISOString();
const stale = new Date(now - 180_000).toISOString();

function agent(overrides: Partial<AgentDevice> = {}): AgentDevice {
  return {
    id: "agent-1", deviceName: "Shop PC", status: "Online",
    createdUtc: recent, lastSeenUtc: recent, printerCount: 1,
    ...overrides
  };
}
function printer(overrides: Partial<PrinterDevice> = {}): PrinterDevice {
  return {
    id: "printer-1", displayName: "USB Printer", manufacturer: "Test",
    model: "Test driver", connectionType: "WindowsQueue", outputMode: "PHYSICAL",
    capabilitiesJson: "{}", status: "Ready", isActive: true, isDefault: true,
    agentDeviceId: "agent-1", lastSeenUtc: recent,
    ...overrides
  };
}

test("physical queues are available only when Agent and Windows queue are fresh and Ready or Busy", () => {
  assert.equal(isPrinterAvailableForShop(printer(), [agent()], now), true);
  assert.equal(isPrinterAvailableForShop(printer({ status: "Busy" }), [agent()], now), true);
  assert.equal(getPrinterAvailabilityReason(printer({ status: "Offline" }), [agent()], now), "Windows queue reports offline");
  assert.equal(getPrinterAvailabilityReason(printer({ status: "Paused" }), [agent()], now), "Windows queue reports paused");
  assert.equal(getPrinterAvailabilityReason(printer({ lastSeenUtc: stale }), [agent()], now), "Windows printer queue has not reported recently");
});

test("an online shop computer does not make a stale or revoked Agent look available", () => {
  assert.equal(getPrinterAvailabilityReason(printer(), [agent({ lastSeenUtc: stale })], now), "Shop computer offline");
  assert.equal(getPrinterAvailabilityReason(printer(), [agent({ status: "Offline" })], now), "Shop computer offline");
  assert.equal(getPrinterAvailabilityReason(printer(), [agent({ revokedUtc: recent })], now), "Shop computer offline");
});

test("only selected supported queues are reported as available", () => {
  assert.equal(getPrinterAvailabilityReason(printer({ isActive: false }), [agent()], now), "Not selected for new jobs");
  assert.equal(getPrinterAvailabilityReason(printer({ outputMode: "UNSUPPORTED_VIRTUAL" }), [agent()], now), "Unsupported Windows output queue");
});

test("PDF export availability follows the live Agent rather than physical queue readiness", () => {
  assert.equal(isPrinterAvailableForShop(printer({ outputMode: "PDF_EXPORT", status: "Unknown", lastSeenUtc: null }), [agent()], now), true);
  assert.equal(getPrinterAvailabilityReason(printer({ outputMode: "PDF_EXPORT" }), [agent({ lastSeenUtc: stale })], now), "Shop computer offline");
});
