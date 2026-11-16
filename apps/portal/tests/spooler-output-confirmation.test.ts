import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  canConfirmPhysicalOutput,
  canManuallyReconcileUncertainOutput,
  hasVerifiedAgentSpoolerObservation,
  isFreshSpoolerObservation,
  isVerifiedSpoolerAbsenceReport,
  isPhysicalOutputConfirmationStatus,
  isRetainedSpoolerJob
} from "../src/spooler-output-confirmation.ts";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const submittedUtc = "2026-11-16T09:00:00.000Z";
const observedUtc = "2026-11-16T09:00:20.000Z";
const nowUtc = Date.parse("2026-11-16T09:01:00.000Z");

test("detects a retained Windows spooler job regardless of casing or surrounding whitespace", () => {
  assert.equal(isRetainedSpoolerJob("RETAINED"), true);
  assert.equal(isRetainedSpoolerJob("retained"), true);
  assert.equal(isRetainedSpoolerJob(" Retained "), true);
});

test("only submitted, printing and output-unconfirmed physical jobs are confirmation candidates", () => {
  for (const status of ["SPOOLER_ACCEPTED", "PRINTING", "OUTPUT_UNCONFIRMED"]) assert.equal(isPhysicalOutputConfirmationStatus(status), true, status);
  for (const status of [null, undefined, "", "AWAITING_PAYMENT", "COMPLETED", "PDF_EXPORTED"]) assert.equal(isPhysicalOutputConfirmationStatus(status), false, String(status));
});

test("spooler observations must be after submission, not future-dated, and no older than ten minutes", () => {
  assert.equal(isFreshSpoolerObservation(submittedUtc, observedUtc, nowUtc), true);
  assert.equal(isFreshSpoolerObservation(submittedUtc, "2026-11-16T08:59:59.000Z", nowUtc), false);
  assert.equal(isFreshSpoolerObservation(submittedUtc, "2026-11-16T09:01:01.000Z", nowUtc), false);
  assert.equal(isFreshSpoolerObservation("2026-11-16T08:40:00.000Z", "2026-11-16T08:50:59.000Z", nowUtc), false);
  assert.equal(isFreshSpoolerObservation(null, observedUtc, nowUtc), false);
  assert.equal(isFreshSpoolerObservation(submittedUtc, "not-a-date", nowUtc), false);
});

test("confirmation requires a recognized, fresh Agent observation", () => {
  assert.equal(hasVerifiedAgentSpoolerObservation("QUEUED", submittedUtc, observedUtc, null, 17, nowUtc), true);
  assert.equal(hasVerifiedAgentSpoolerObservation("OUTPUT_UNCONFIRMED", submittedUtc, observedUtc, "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, nowUtc), true);
  assert.equal(hasVerifiedAgentSpoolerObservation("OUTPUT_UNCONFIRMED", submittedUtc, observedUtc, "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, nowUtc), false);
  assert.equal(hasVerifiedAgentSpoolerObservation("PRINTER_UNAVAILABLE", submittedUtc, observedUtc, null, 17, nowUtc), false);
  assert.equal(hasVerifiedAgentSpoolerObservation("UNKNOWN", submittedUtc, observedUtc, null, 17, nowUtc), false);
  assert.equal(hasVerifiedAgentSpoolerObservation("QUEUED", submittedUtc, null, null, 17, nowUtc), false);
});

test("RETAINED blocks confirmation in every physical-output state", () => {
  for (const status of ["SPOOLER_ACCEPTED", "PRINTING", "OUTPUT_UNCONFIRMED"]) {
    assert.equal(canConfirmPhysicalOutput(status, "RETAINED", submittedUtc, observedUtc, "Ready", null, 17, nowUtc), false, status);
  }
});

test("only a terminal PRINTED observation can authorize routine physical confirmation", () => {
  for (const status of ["SPOOLER_ACCEPTED", "PRINTING", "OUTPUT_UNCONFIRMED"]) {
    assert.equal(canConfirmPhysicalOutput(status, "PRINTED", submittedUtc, observedUtc, "Ready", null, 17, nowUtc), true, status);
  }
  for (const status of ["QUEUED", "SPOOLING", "PRINTING", "OFFLINE", "PAPEROUT", "PAUSED", "ERROR", "USERINTERVENTION"]) {
    assert.equal(canConfirmPhysicalOutput("PRINTING", status, submittedUtc, observedUtc, "Ready", null, 17, nowUtc), false, status);
  }
});

test("verified queue absence retains manual physical-page resolution", () => {
  assert.equal(isVerifiedSpoolerAbsenceReport("SPOOLER_JOB_NO_LONGER_VISIBLE", 17), true);
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", submittedUtc, observedUtc, "Ready", "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, nowUtc), true);
  assert.equal(canConfirmPhysicalOutput("SPOOLER_ACCEPTED", "OUTPUT_UNCONFIRMED", submittedUtc, observedUtc, "Ready", "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, nowUtc), false);
});

test("an Agent restart without a spooler ID is uncertain and requires manual queue reconciliation", () => {
  assert.equal(isVerifiedSpoolerAbsenceReport("AGENT_RESTART_DURING_PRINT_SUBMISSION", null), false);
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "UNKNOWN", submittedUtc, observedUtc, "Ready", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, nowUtc), false);
  assert.equal(canManuallyReconcileUncertainOutput("OUTPUT_UNCONFIRMED", "UNKNOWN", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, "HP LaserJet Queue", "SmartPrint-SP-12345"), true);
  assert.equal(canManuallyReconcileUncertainOutput("OUTPUT_UNCONFIRMED", "RETAINED", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, "HP LaserJet Queue", "SmartPrint-SP-12345"), false);
  assert.equal(canManuallyReconcileUncertainOutput("OUTPUT_UNCONFIRMED", "UNKNOWN", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, "HP LaserJet Queue", null), false);
  assert.equal(canManuallyReconcileUncertainOutput("SPOOLER_ACCEPTED", "UNKNOWN", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, "HP LaserJet Queue", "SmartPrint-SP-12345"), false);
  assert.equal(canManuallyReconcileUncertainOutput("OUTPUT_UNCONFIRMED", "PRINTED", "AGENT_RESTART_DURING_PRINT_SUBMISSION", null, "HP LaserJet Queue", "SmartPrint-SP-12345"), false);
});

test("stale observations cannot authorize output confirmation", () => {
  assert.equal(canConfirmPhysicalOutput("PRINTING", "PRINTED", "2026-11-16T08:40:00.000Z", "2026-11-16T08:50:59.000Z", "Ready", null, 17, nowUtc), false);
});

test("printer warnings still block routine confirmation, but verified queue absence can be manually inspected", () => {
  for (const printerStatus of ["Offline", "Paused"]) {
    assert.equal(canConfirmPhysicalOutput("PRINTING", "PRINTED", submittedUtc, observedUtc, printerStatus, null, 17, nowUtc), false);
  }
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", submittedUtc, observedUtc, "Offline", "SPOOLER_JOB_NO_LONGER_VISIBLE", 17, nowUtc), true);
});

test("Order Journey and Print Queue use the same physical-output eligibility and manual-reconciliation helpers", () => {
  const orderJourney = read("../src/OrderJourneyPage.tsx");
  const printQueue = read("../src/App.tsx");
  const expected = /canConfirmPhysicalOutput\(\s*job\.status,\s*job\.spoolerStatus,\s*job\.spoolerSubmittedUtc,\s*job\.spoolerObservedUtc,\s*job\.printerStatus,\s*job\.failureCode,\s*job\.spoolerJobId\s*\)/;
  for (const source of [orderJourney, printQueue]) {
    assert.match(source, /isPhysicalOutputConfirmationStatus\(job\.status\)/);
    assert.match(source, expected);
    assert.match(source, /canManuallyReconcileUncertainOutput\(\s*job\.status/);
  }
});

test("initial Agent acceptance receipts cannot supply terminal PRINTED proof", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const routeStart = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/spooler-accepted"');
  const routeEnd = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/print-blocked"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart);
  const route = program.slice(routeStart, routeEnd);
  assert.match(route, /NormalizeInitialSpoolerAcceptanceStatus\(request\.SpoolerStatus\)/);
  assert.doesNotMatch(route, /job\.SpoolerStatus\s*=\s*request\.SpoolerStatus/);

  const monitor = read("../../../src/SmartPrint.Agent/Worker.cs");
  const monitorStart = monitor.indexOf("private async Task MonitorSpoolerJobsAsync");
  const monitorEnd = monitor.indexOf("private async Task RunPrivateSpoolerCleanupLoopAsync", monitorStart);
  assert.ok(monitorStart >= 0 && monitorEnd > monitorStart);
  const polling = monitor.slice(monitorStart, monitorEnd);
  assert.ok(polling.indexOf("printExecutor.GetJob(") < polling.indexOf("ReportSpoolerStatusAsync("),
    "Only a fresh Windows GetJob poll may submit the terminal spooler state.");
});

test("the API gates completion and private-source purge on fresh output proof or an explicit queue reconciliation", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const routeStart = program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/confirm-output"');
  const routeEnd = program.indexOf('shop.MapGet("/print-jobs/{jobId:guid}/events"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart, "The physical output-confirmation endpoint must exist.");
  const route = program.slice(routeStart, routeEnd);
  const verified = route.indexOf("SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(");
  const manual = route.indexOf("SpoolerOutputConfirmationPolicy.CanManuallyReconcileUncertainOutput(");
  const completed = route.indexOf('job.Status = "COMPLETED"');
  const purge = route.indexOf("PrivateDocumentPurgeRequestedUtc");
  assert.ok(verified >= 0 && manual >= 0 && completed > verified && completed > manual && purge > verified && purge > manual);
  assert.match(route, /manualQueueReconciliationConfirmed/);
  assert.match(route, /SHOP_OPERATOR_CONFIRMED_PHYSICAL_OUTPUT_AFTER_QUEUE_RECONCILIATION/);
});
