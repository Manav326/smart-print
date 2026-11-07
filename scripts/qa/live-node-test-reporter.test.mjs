import assert from "node:assert/strict";
import { Readable } from "node:stream";
import test from "node:test";
import liveNodeTestReporter from "./live-node-test-reporter.mjs";

async function render(events) {
  const output = [];
  for await (const chunk of liveNodeTestReporter(Readable.from(events))) {
    output.push(String(chunk));
  }
  return output.join("");
}

test("live reporter announces the exact case before execution and reports its outcome", async () => {
  const output = await render([
    { type: "test:dequeue", data: { name: "rejects a cross-tenant upload", file: "tests/security.test.mjs", line: 42, nesting: 0 } },
    { type: "test:complete", data: { name: "rejects a cross-tenant upload", file: "tests/security.test.mjs", line: 42, nesting: 0, details: { passed: true, duration_ms: 18.6 } } }
  ]);

  assert.match(output, /\[TEST 001 START\].*rejects a cross-tenant upload.*security\.test\.mjs:42/);
  assert.match(output, /\[TEST PASS\].*rejects a cross-tenant upload.*19 ms/);
});

test("live reporter identifies failures, errors, skipped tests and run totals", async () => {
  const output = await render([
    { type: "test:dequeue", data: { name: "rejects malformed document", file: "documents.test.mjs", line: 9 } },
    { type: "test:complete", data: { name: "rejects malformed document", file: "documents.test.mjs", line: 9, details: { passed: false, duration_ms: 31, error: { message: "Unexpected file signature" } } } },
    { type: "test:dequeue", data: { name: "handles optional recovery", file: "recovery.test.mjs", line: 20 } },
    { type: "test:complete", data: { name: "handles optional recovery", file: "recovery.test.mjs", line: 20, skip: true, details: { passed: true, duration_ms: 0 } } },
    { type: "test:summary", data: { counts: { tests: 2, passed: 1, failed: 1, skipped: 1, todo: 0 }, duration_ms: 40 } }
  ]);

  assert.match(output, /\[TEST FAIL\].*rejects malformed document/);
  assert.match(output, /\[TEST ERROR\] Unexpected file signature/);
  assert.match(output, /\[TEST SKIP\].*handles optional recovery/);
  assert.match(output, /tests=2 passed=1 failed=1 skipped=1/);
});
