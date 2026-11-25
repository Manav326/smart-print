import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const api = read("../../../src/SmartPrint.Api/Program.cs");
const portalApi = read("../src/api.ts");
const agentApi = read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
const agentWorker = read("../../../src/SmartPrint.Agent/Worker.cs");
const customerApp = read("../src/App.tsx");
const privateRelayRules = read("../../../src/SmartPrint.Api/Domain/PrivateRelayProgressRules.cs");
const localAgentApi = read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
const nginx = read("../nginx.conf");

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


test("cloud document transfer routes stream through Nginx with the matching multipart ceiling", () => {
  assert.ok(nginx.includes("client_max_body_size 22m;"));
  assert.ok(nginx.includes("upstream smart_print_api {"));
  const transferStart = nginx.indexOf('    location ~ "^/api/v1/');
  const transferEnd = nginx.indexOf("\n    }", transferStart);
  assert.ok(transferStart >= 0 && transferEnd > transferStart, "the dedicated transfer location must exist");
  const transferLocation = nginx.slice(transferStart, transferEnd);
  assert.ok(transferLocation.includes("sessions/current/jobs"));
  assert.ok(transferLocation.includes("private-relay|cloud-recovery"));
  assert.ok(transferLocation.includes("[0-9a-fA-F-]{36}/document"));
  assert.ok(transferLocation.includes("[0-9a-fA-F-]{36}/content"));
  assert.ok(transferLocation.includes("proxy_pass http://smart_print_api;"));
  assert.ok(transferLocation.includes("client_max_body_size 51m;"));
  assert.ok(transferLocation.includes("proxy_request_buffering off;"));
  assert.ok(transferLocation.includes("proxy_buffering off;"));
  assert.ok(transferLocation.includes("proxy_read_timeout 10m;"));
});


test("customer retry copy preserves ticket identity without promising byte-offset resume", () => {
  assert.match(portalApi, /may need to be sent again from the beginning/i);
  assert.match(portalApi, /no second print job will be created/i);
  assert.doesNotMatch(portalApi, /resume the same (?:job|request|file)/i);
  assert.match(customerApp, /may need to be sent again from the beginning/i);
  assert.doesNotMatch(customerApp, /the same upload will resume|resume that same upload/i);
});

test("shop queue labels distinguish Agent preparation from print readiness", () => {
  assert.match(customerApp, /LOCAL_QUEUE_CLAIMED:"Shop computer preparing"/);
  assert.match(customerApp, /LOCAL_QUEUE_READY:"Ready for print"/);
});

test("Private Printing progress uses short, plain-language customer messages", () => {
  const start = privateRelayRules.indexOf("public static string CustomerMessage");
  assert.ok(start >= 0, "customer-facing stage messages must remain defined");
  const messages = Array.from(privateRelayRules.slice(start).matchAll(/"([^"]*)"/g))
    .map(([, message]) => message)
    .join("\n");
  assert.match(messages, /Waiting to send your secure file/);
  assert.match(messages, /Checking file security/);
  assert.match(messages, /Checking that the file can be printed/);
  assert.match(messages, /File secured on the shop computer · ready for printing/);
  assert.doesNotMatch(messages, /decrypting|authenticating|encrypted payload|local print queue/i);
});

test("prepared ID sheets defer batch privacy decisions to authoritative persisted jobs and tickets", () => {
  const localTicketStart = api.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"');
  const localTicketEnd = api.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/', localTicketStart + 1);
  assert.ok(localTicketStart >= 0, "the local upload-ticket admission route must exist");
  const ticketRoute = api.slice(localTicketStart, localTicketEnd > localTicketStart ? localTicketEnd : undefined);
  assert.match(ticketRoute, /job\.BatchId == batchId && job\.PrivatePrinting != request\.PrivatePrinting/);
  assert.match(ticketRoute, /ticket\.BatchId == batchId && ticket\.PrivatePrinting != request\.PrivatePrinting/);
  assert.match(ticketRoute, /code = "batch_privacy_mode_mismatch"/);
  assert.match(portalApi, /privatePrinting,/);
  assert.match(portalApi, /printKind,/);
  assert.doesNotMatch(customerApp, /uploadedBatch\.every\(item=>item\.job\.privatePrinting===true\)/,
    "a stale or incomplete cached DTO must not pre-empt the API's authoritative batch-mode check");
  assert.match(customerApp, /batchPrivacyMismatch=exception instanceof ApiError&&exception\.code==="batch_privacy_mode_mismatch"/);
  assert.match(customerApp, /This batch already contains a Standard Printing file/);

  const successfulLocalAgentResponse = api.slice(
    api.lastIndexOf("AlreadyCompleted = false,"),
    api.indexOf('agentApi.MapPost("/local-uploads/status-batch"')
  );
  assert.match(successfulLocalAgentResponse, /job\.PrivatePrinting/,
    "the first successful local Agent upload response must return the persisted privacy mode");
});
