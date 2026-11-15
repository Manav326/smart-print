import assert from "node:assert/strict";
import test from "node:test";
import { isRetainedSpoolerJob } from "../src/spooler-output-confirmation.ts";

test("detects a retained Windows spooler job regardless of casing or surrounding whitespace", () => {
  assert.equal(isRetainedSpoolerJob("RETAINED"), true);
  assert.equal(isRetainedSpoolerJob("retained"), true);
  assert.equal(isRetainedSpoolerJob(" Retained "), true);
});

test("does not block confirmation after the Agent reports that the retained state cleared", () => {
  for (const status of [null, undefined, "", "OUTPUT_UNCONFIRMED", "PRINTED", "QUEUED", "PRINTER_UNAVAILABLE"]) {
    assert.equal(isRetainedSpoolerJob(status), false, String(status));
  }
});
