import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import {
  canConfirmPhysicalOutput,
  isPhysicalOutputConfirmationStatus,
  isRetainedSpoolerJob
} from "../src/spooler-output-confirmation.ts";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");

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

test("RETAINED blocks confirmation in every physical-output state, including OUTPUT_UNCONFIRMED", () => {
  for (const jobStatus of ["SPOOLER_ACCEPTED", "PRINTING", "OUTPUT_UNCONFIRMED"]) {
    assert.equal(canConfirmPhysicalOutput(jobStatus, "RETAINED"), false, jobStatus);
  }
});

test("a fresh non-retained Agent observation makes an output-unconfirmed job eligible again", () => {
  for (const spoolerStatus of ["OUTPUT_UNCONFIRMED", "PRINTED", "QUEUED"]) {
    assert.equal(canConfirmPhysicalOutput("OUTPUT_UNCONFIRMED", spoolerStatus), true, spoolerStatus);
  }
});

test("Order Journey and Print Queue use the same physical-output eligibility helper", () => {
  const orderJourney = read("../src/OrderJourneyPage.tsx");
  const printQueue = read("../src/App.tsx");
  for (const source of [orderJourney, printQueue]) {
    assert.match(source, /isPhysicalOutputConfirmationStatus\(job\.status\)/);
    assert.match(source, /canConfirmPhysicalOutput\(job\.status,\s*job\.spoolerStatus\)/);
  }
});

test("the API endpoint applies the server guard before completion or private-source purge", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const routeStart = program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/confirm-output"');
  const routeEnd = program.indexOf('shop.MapGet("/print-jobs/{jobId:guid}/events"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart, "The physical output-confirmation endpoint must exist.");
  const route = program.slice(routeStart, routeEnd);
  const guard = route.indexOf("SpoolerOutputConfirmationPolicy.CanConfirmPhysicalOutput(job.Status, job.SpoolerStatus)");
  const completed = route.indexOf('job.Status = "COMPLETED"');
  const purge = route.indexOf("PrivateDocumentPurgeRequestedUtc");
  assert.ok(guard >= 0 && completed > guard && purge > guard,
    "The server must reject retained jobs before marking them complete or requesting private-source purge.");
});
