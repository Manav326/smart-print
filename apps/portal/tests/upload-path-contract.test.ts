import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const api = read("../../../src/SmartPrint.Api/Program.cs");
const portalApi = read("../src/api.ts");
const agentApi = read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
const agentWorker = read("../../../src/SmartPrint.Agent/Worker.cs");
const localAgentApi = read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");

test("standard cloud multipart uploads enforce the per-file and aggregate batch limits", () => {
  assert.match(api, /file\.Length is <= 0 or > UploadLimits\.MaxFileBytes/);
  assert.match(api, /PrintBatchCapacityRules\.CanAdd\(/);
  assert.match(api, /batchJobSizes\.Count\s*\+\s*pendingBatchTicketSizes\.Count/);
  assert.match(api, /batchJobSizes\.Sum\(\)\s*\+\s*pendingBatchTicketSizes\.Sum\(\)/);
});

test("local ticket reservation enforces the same 50 MiB, 20-file and 500 MiB limits", () => {
  assert.match(api, /request\.SizeBytes is <= 0 or > UploadLimits\.MaxFileBytes/);
  assert.match(api, /PrintBatchCapacityRules\.CanAdd\(/);
  assert.match(api, /UploadLimits\.MaxBatchFiles/);
  assert.match(api, /UploadLimits\.MaxBatchBytes/);
});

test("direct Agent LAN upload validates the ticket size before accepting either QSP2 or legacy payloads", () => {
  assert.match(agentApi, /claim\.SizeBytes is <= 0 or > UploadLimits\.MaxFileBytes/);
  assert.match(agentApi, /Qsp2DocumentFormat\.GetEncryptedLength\(claim\.SizeBytes\)/);
  assert.match(agentApi, /claim\.SizeBytes\s*\+\s*UploadLimits\.PrivateRelayEncryptionOverheadBytes/);
  assert.match(agentApi, /localQueueStore\.EnqueueLocalUploadStreamingAsync\(/);
});

test("encrypted Private Printing relay and worker enforce the original-file ceiling", () => {
  assert.match(api, /encryptedSize != Qsp2DocumentFormat\.GetEncryptedLength\(ticket\.SizeBytes\)/);
  assert.match(agentWorker, /Qsp2DocumentFormat\.DecryptAsync\([\s\S]{0,300}UploadLimits\.MaxFileBytes/);
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
test("Private Printing publishes durable, authenticated stage updates for direct LAN and cloud relay processing", () => {
  assert.match(api, /MapPost\("\/private-relays\/\{ticketId:guid\}\/stage"/);
  assert.match(api, /hasClaimedDirectLanUpload/);
  assert.match(api, /PrivateRelayProgressRules\.ReceivingEncryptedPayload/);
  assert.match(api, /StageMessage = PrivateRelayProgressRules\.CustomerMessage\(stage\)/);
  assert.match(agentWorker, /ReportPrivateRelayStageAsync\([\s\S]{0,250}DECRYPTING_AND_AUTHENTICATING/);
  assert.match(agentWorker, /ReportPrivateRelayStageAsync\([\s\S]{0,250}VALIDATING_DOCUMENT/);
  assert.match(localAgentApi, /ReportPrivateStageAsync\("DECRYPTING_AND_AUTHENTICATING"\)/);
  assert.match(localAgentApi, /ReportPrivateStageAsync\("VALIDATING_DOCUMENT"\)/);
  assert.match(localAgentApi, /ReportPrivateStageAsync\("SECURING_LOCAL_QUEUE"\)/);
});

test("Private Printing never starts a second relay while the same ticket is claimed for direct LAN transfer", () => {
  assert.match(api, /TransferPath = transferPath/);
  assert.match(api, /ticket\.ClaimedUtc is not null[\s\S]{0,180}ticket\.ClaimLeaseExpiresUtc > now/);
  assert.match(api, /private_relay_direct_transfer_in_progress/);
  assert.match(api, /private_relay_cloud_transfer_in_progress/);
  assert.match(api, /item\.PrivateRelayStage == PrivateRelayProgressRules\.WaitingForUpload[\s\S]{0,120}item\.PrivateRelayStage == PrivateRelayProgressRules\.ReceivingEncryptedPayload/);
  const relay = portalApi.slice(portalApi.indexOf("async function uploadCustomerPrivateRelay"), portalApi.indexOf("function createStreamingLocalMultipartBody"));
  const localUpload = portalApi.slice(portalApi.indexOf("export async function uploadCustomerDocumentLocally"));
  assert.match(localUpload, /if \(privatePrinting\) break/);
  assert.match(relay, /existing\.transferPath === "DIRECT_LAN"/);
  assert.match(relay, /existing\.transferPath === "CLOUD_RELAY"/);
  assert.match(relay, /has not started a second encrypted transfer/);
  const stageRules = read("../../../src/SmartPrint.Api/Domain/PrivateRelayProgressRules.cs");
  assert.match(stageRules, /Rank\(requestedStage\) > Rank\(currentStage\)/);
});
