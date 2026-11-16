import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const api = read("../../../src/SmartPrint.Api/Program.cs");
const agentApi = read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
const agentWorker = read("../../../src/SmartPrint.Agent/Worker.cs");

test("cloud and local ticket creation share the 50 MiB per-file and 500 MiB batch guard", () => {
  assert.match(api, /PrintBatchCapacityRules\.CanAdd\(/);
  assert.match(api, /request\.SizeBytes is <= 0 or > UploadLimits\.MaxFileBytes/);
  assert.match(api, /request\.SizeBytes\)\s*\)\s*\{/);
});

test("Private Printing reserves the original-size ticket and verifies the QSP2 encrypted wire size", () => {
  assert.match(api, /Qsp2DocumentFormat\.GetEncryptedLength\(ticket\.SizeBytes\)/);
  assert.match(api, /encryptedSize != Qsp2DocumentFormat\.GetEncryptedLength\(ticket\.SizeBytes\)/);
  assert.match(agentApi, /Qsp2DocumentFormat\.GetEncryptedLength\(claim\.SizeBytes\)/);
  assert.match(agentApi, /claim\.SizeBytes is <= 0 or > UploadLimits\.MaxFileBytes/);
});

test("the Agent decrypts QSP2 within the 50 MiB plaintext limit before queueing", () => {
  assert.match(agentWorker, /Qsp2DocumentFormat\.DecryptAsync\(/);
  assert.match(agentWorker, /UploadLimits\.MaxFileBytes/);
  assert.match(agentWorker, /EnqueueLocalUploadStreamingAsync\(/);
});

test("shop batch review cannot accept more than twenty files", () => {
  assert.match(api, /reviewedIds\.Length is < 1 or > UploadLimits\.MaxBatchFiles/);
});

test("per-session upload rate budget can carry a complete 20-file Private Printing batch", () => {
  assert.match(api, /PermitLimit = UploadLimits\.MaxBatchFiles \* 3/);
});
