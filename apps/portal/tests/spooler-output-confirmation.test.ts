import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  canConfirmPhysicalOutput,
  hasVerifiedAgentSpoolerObservation,
  isVerifiedSpoolerAbsenceReport,
  isPhysicalOutputConfirmationStatus,
  isRetainedSpoolerJob
} from "../src/spooler-output-confirmation.ts";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const observedUtc = "2026-11-16T09:00:00.000Z";

test("detects a retained Windows spooler job regardless of casing or surrounding whitespace", () => {
  assert.equal(isRetainedSpoolerJob("RETAINED"), true);
  assert.equal(isRetainedSpoolerJob("retained"), true);
  assert.equal(isRetainedSpoolerJob(" Retained "), true);
});

test("only submitted, printing and output-unconfirmed physical jobs are confirmation candidates", () => {
  for (const status of ["SPOOLER_ACCEPTED", "PRINTING", "OUTPUT_UNCONFIRMED"]) {
    assert.equal(isPhysicalOutputConfirmationStatus(status), true, status);
  }
  for (const status of [null, undefined, "", "AWAITING_PAYMENT", "COMPLETED", "PDF_EXPORTED"]) {
    assert.equal(isPhysicalOutputConfirmationStatus(status), false, String(status));
  }
});

test("confirmation requires a recognized Agent spooler observation and its timestamp", () => {
  assert.equal(hasVerifiedAgentSpoolerObservation("QUEUED", observedUtc), true);
  assert.equal(hasVerifiedAgentSpoolerObservation("OUTPUT_UNCONFIRMED", observedUtc), true);
  assert.equal(hasVerifiedAgentSpoolerObservation("PRINTER_UNAVAILABLE", observedUtc), false);
  assert.equal(hasVerifiedAgentSpoolerObservation("UNKNOWN", observedUtc), false);
  assert.equal(hasVerifiedAgentSpoolerObservation("QUEUED", null), false);
  assert.equal(hasVerifiedAgentSpoolerObservation("QUEUED", "not-a-date"), false);
});

test("RETAINED blocks confirmation in every physical-output state, including OUTPUT_UNCONFIRMED", () => {
  for (const jobStatus of ["SPOOLER_ACCEPTED", "PRINTING", "OUTPUT_UNCONFIRMED"]) {
    assert.equal(canConfirmPhysicalOutput(jobStatus, "RETAINED", observedUtc, "Ready"), false, jobStatus);
  }
});

test("a fresh non-retained Agent observation clears an obsolete RETAINED block", () => {
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", observedUtc, "Ready", "SPOOLER_JOB_NO_LONGER_VISIBLE", 17), true);
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "PRINTED", observedUtc, "Ready"), true);
});

test("legacy OUTPUT_UNCONFIRMED status is not trusted without a verified absence reason", () => {
  assert.equal(isVerifiedSpoolerAbsenceReport("SPOOLER_JOB_NO_LONGER_VISIBLE", 17), true);
  assert.equal(isVerifiedSpoolerAbsenceReport("AGENT_RESTART_DURING_PRINT_SUBMISSION", null), true);
  assert.equal(isVerifiedSpoolerAbsenceReport("UNEXPECTED_PRINT_EXECUTION_ERROR", 17), false);
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", observedUtc, "Ready", "UNEXPECTED_PRINT_EXECUTION_ERROR", 17), false);
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "OUTPUT_UNCONFIRMED", observedUtc, "Ready", "SPOOLER_JOB_NO_LONGER_VISIBLE", 17), true);
});

test("routine confirmation remains blocked by Windows and printer warnings", () => {
  for (const spoolerStatus of ["OFFLINE", "PAPEROUT", "PAUSED", "ERROR", "USERINTERVENTION"]) {
    assert.equal(canConfirmPhysicalOutput("SPOOLER_ACCEPTED", spoolerStatus, observedUtc, "Ready"), false, spoolerStatus);
  }
  for (const printerStatus of ["Offline", "Paused"]) {
    assert.equal(canConfirmPhysicalOutput("PRINTING", "QUEUED", observedUtc, printerStatus), false, printerStatus);
  }
});

test("OUTPUT_UNCONFIRMED remains the manual physical-output resolution path after a verified Agent report", () => {
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "ERROR", observedUtc, "Offline"), true);
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "PRINTER_UNAVAILABLE", observedUtc, "Ready"), false);
  assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", "RETAINED", observedUtc, "Ready"), false);
});

test("Order Journey and Print Queue use the same physical-output eligibility helper", () => {
  const orderJourney = read("../src/OrderJourneyPage.tsx");
  const printQueue = read("../src/App.tsx");
  const expected = /canConfirmPhysicalOutput\(job\.status,\s*job\.spoolerStatus,\s*job\.spoolerObservedUtc,\s*job\.printerStatus,\s*job\.failureCode,\s*job\.spoolerJobId\)/;
  for (const source of [orderJourney, printQueue]) {
    assert.match(source, /isPhysicalOutputConfirmationStatus\(job\.status\)/);
    assert.match(source, expected);
  }
  assert.doesNotMatch(printQueue, /job\.status!==\"OUTPUT_UNCONFIRMED\"&&!!spoolerStatusWarning/);
});

test("the Agent only clears a retained spooler report after a verified absence report", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const routeStart = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed"');
  const routeEnd = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/spooler-status"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart, "The Agent output-reconciliation endpoint must exist.");
  const route = program.slice(routeStart, routeEnd);
  assert.match(route, /IsVerifiedSpoolerAbsenceReport\(reason,\s*job\.SpoolerJobId\)/);
  const guardedBlocks = [...route.matchAll(/if \(verifiedSpoolerAbsence\)\s*\{([\s\S]*?)\}/g)].map(match => match[1]);
  assert.equal(guardedBlocks.length, 2, "Both first and duplicate Agent reports must gate spooler-state refresh.");
  for (const block of guardedBlocks) {
    assert.match(block, /job\.SpoolerStatus = "OUTPUT_UNCONFIRMED"/);
    assert.match(block, /job\.SpoolerObservedUtc = now/);
  }
  assert.equal((route.match(/job\.SpoolerStatus = "OUTPUT_UNCONFIRMED"/g) ?? []).length, 2,
    "No unverified output-uncertain reason may manufacture a verified spooler observation.");
});

test("the API endpoint applies the server guard before completion or private-source purge", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const routeStart = program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/confirm-output"');
  const routeEnd = program.indexOf('shop.MapGet("/print-jobs/{jobId:guid}/events"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart, "The physical output-confirmation endpoint must exist.");
  const route = program.slice(routeStart, routeEnd);
  const guard = route.indexOf("SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(job.Status, job.SpoolerStatus, job.SpoolerObservedUtc, printerStatus, job.FailureCode, job.SpoolerJobId)");
  const completed = route.indexOf('job.Status = "COMPLETED"');
  const purge = route.indexOf("PrivateDocumentPurgeRequestedUtc");
  assert.ok(route.includes("HasVerifiedAgentSpoolerObservation(job.SpoolerStatus, job.SpoolerObservedUtc)"));
  assert.ok(guard >= 0 && completed > guard && purge > guard,
    "The server must reject retained or unverified jobs before completion or private-source purge.");
});
