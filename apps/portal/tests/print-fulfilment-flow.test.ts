import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");

test("customer upload sends the selected file settings to both storage paths", () => {
  const api = read("../src/api.ts");
  assert.match(api, /form\.append\("colourMode", settings\.colourMode\)/);
  assert.match(api, /form\.append\("paperSize", settings\.paperSize\)/);
  assert.match(api, /form\.append\("isDuplex", String\(settings\.isDuplex\)\)/);
  assert.match(api, /form\.append\("copies", String\(settings\.copies\)\)/);
  assert.match(api, /form\.append\("orientation", settings\.orientation\)/);
  assert.match(api, /orientation: settings\.orientation/);
});

test("Private Printing relays only the encrypted print document and required quote metadata", () => {
  const api = read("../src/api.ts");
  const ticketStart = api.indexOf("async function requestCustomerLocalUploadTicket(");
  const ticketEnd = api.indexOf("async function uploadCustomerLocalTicketViaCloud(", ticketStart);
  assert.ok(ticketStart >= 0 && ticketEnd > ticketStart);
  const ticket = api.slice(ticketStart, ticketEnd);
  assert.match(ticket, /originalFileName: privatePrinting \? "private-document" \+ privateExtension/);
  assert.match(ticket, /privatePrinting,/);
  assert.match(ticket, /photoSizeCode: photoSettings\?\.sizeCode/);
  assert.match(ticket, /photoCount: photoSettings\?\.photoCount/);
  assert.match(ticket, /photosPerPage: photoSettings\?\.photosPerPage/);
  assert.doesNotMatch(ticket, /editorSettingsJson|PhotoEditorSettings|cropX|cropY|zoom/);

  const uploadStart = api.indexOf("export async function uploadCustomerDocumentLocally(");
  const upload = api.slice(uploadStart);
  const qsp2 = read("../src/private-qsp2-upload.ts");
  assert.match(api, /preparePrivateQsp2Upload.*from "\.\/private-qsp2-upload"/);
  assert.match(upload, /privateUpload = await preparePrivateQsp2Upload\(file,\s*\{/);
  assert.match(upload, /const attempt = await privateUpload\.createAttempt\(\)/);
  assert.match(upload, /requestHeaders\["X-Print-Encrypted-Format"\] = "QSP2"/);
  assert.match(upload, /requestHeaders\["X-Print-Encrypted-Key"\] = attempt\.wrappedKey/);
  assert.match(upload, /requestBody = attempt\.body/);
  assert.match(qsp2, /const QSP2_RECORD_BYTES = 1024 \* 1024/);
  assert.match(qsp2, /name: "AES-GCM"/);
  assert.match(qsp2, /return new ReadableStream<Uint8Array>/);
  assert.match(qsp2, /name: "RSA-OAEP"/);
  const relayStart = upload.indexOf("if (privatePrinting) {", upload.indexOf("if (ticket.agentDeviceId === localAgent.agentDeviceId"));
  const relayEnd = upload.indexOf("// A LAN response can be lost", relayStart);
  assert.ok(relayStart >= 0 && relayEnd > relayStart);
  const privateRelay = upload.slice(relayStart, relayEnd);
  assert.match(privateRelay, /return uploadCustomerPrivateRelay\(/);
  assert.doesNotMatch(privateRelay, /uploadCustomerLocalTicketViaCloud/);
});

test("standard Cloud-assisted Photo Studio uploads keep photo identity through the existing multipart form", () => {
  const api = read("../src/api.ts");
  const app = read("../src/App.tsx");
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const cloudStart = api.indexOf("export async function uploadCustomerDocument(");
  const cloudEnd = api.indexOf("type CustomerLocalAgentApi", cloudStart);
  const cloudUpload = api.slice(cloudStart, cloudEnd);
  assert.ok(cloudStart >= 0 && cloudEnd > cloudStart);
  assert.match(cloudUpload, /printKind: "DOCUMENT" \| "PHOTO" \| "ID_DOCUMENT" = "DOCUMENT"/);
  assert.match(cloudUpload, /form\.append\("printKind", printKind\)/);
  assert.match(cloudUpload, /form\.append\("photoSizeCode", photoSettings\.sizeCode\)/);
  assert.match(cloudUpload, /form\.append\("photoCount", String\(photoSettings\.photoCount\)\)/);
  assert.match(cloudUpload, /form\.append\("photosPerPage", String\(photoSettings\.photosPerPage \?\? 1\)\)/);
  assert.match(app, /await uploadCustomerDocument\(sessionToken,candidate,currentBatchId,batchGroupName,currentBatchName\|\|undefined,settings,printKind,photoSettings\)/);
  const routeStart = program.indexOf('publicApi.MapPost("/sessions/current/jobs"');
  const routeEnd = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart);
  const route = program.slice(routeStart, routeEnd);
  assert.match(route, /PhotoSheetPdfRules\.HasExactlyPhotoPages/);
  assert.match(route, /PrintKind = requestedPrintKind/);
  assert.match(route, /PhotoSizeCode = photoSizeCode/);
  assert.match(route, /PhotoCount = photoCount \?\? 0/);
  assert.match(route, /PhotosPerPage = photosPerPage/);
  assert.match(route, /existing\.PhotoCount != \(photoCount \?\? 0\)/);
});

test("private Photo Studio vault operations are scoped to the dedicated native route", () => {
  const main = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
  assert.match(main, /privatePhotoStudioPageOpen &&[\s\S]*isPhotoStudioScreenUrl\(webView\?\.url\)/);
  assert.match(main, /photoStudioLibraryCommand && !photoStudioRouteActive/);
});

test("saved Photo Studio sheets join the chooser selection and wait for the final Add action", () => {
  const main = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
  const app = read("../src/App.tsx");
  const start = main.indexOf("private fun addPhotoStudioPickerSection(parent: LinearLayout)");
  const end = main.indexOf("\n    private fun ", start + 1);
  assert.ok(start >= 0 && end > start);
  const picker = main.slice(start, end);
  assert.match(picker, /Nothing is attached until you confirm below/);
  assert.match(picker, /pickerStudioSelectionButton\(file, "Photo Studio document", 88\)/);
  assert.doesNotMatch(picker, /chooseFiles\(listOf\(file\)/);
  assert.doesNotMatch(picker, /uploadCustomerDocument|dispatchShopPrintJob/);
  assert.match(main, /Add selected files \(\$\{chooserSelection\.size\}\)/);
  assert.match(main, /setChooserFileSelected\(file, !selected\)/);
  assert.match(app, /privatePrinting\?"Upload "\+pendingFilesCount\+" file\(s\) securely"/);
});

test("successful upload releases decrypted ID and Photo Studio staging but preserves both encrypted libraries", () => {
  const app = read("../src/App.tsx");
  const main = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
  const photoStore = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/PhotoStudioStore.kt");
  const idStore = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/IdStudioStore.kt");
  assert.match(app, /setFiles\(\[\]\);[\s\S]*postSmartPrintNativeMessage\("releaseStudioUploadFiles"\)/);
  assert.match(main, /data == "releaseStudioUploadFiles"/);
  assert.match(main, /privateIdUploadTempFiles\.toList\(\)\.forEach\(idStudioStore::releaseTransientFile\)/);
  assert.match(main, /privateIdUploadTempFiles\.clear\(\)/);
  assert.match(main, /privatePhotoUploadTempFiles\.toList\(\)\.forEach\(photoStudioStore::releaseTransientFile\)/);
  assert.match(main, /privatePhotoUploadTempFiles\.clear\(\)/);
  const previewCleanupStart = main.indexOf("private fun clearCustomerUploadPdfPreviews()");
  const previewCleanupEnd = main.indexOf("private fun cancelPicker()", previewCleanupStart);
  assert.ok(previewCleanupStart >= 0 && previewCleanupEnd > previewCleanupStart);
  const previewCleanup = main.slice(previewCleanupStart, previewCleanupEnd);
  assert.match(previewCleanup, /photoStudioStore\.releaseTransientFile\(file\)/);
  assert.match(previewCleanup, /if \(parent == previewDirectory\) file\.delete\(\)/);
  assert.match(photoStore, /fun releaseTransientFile\(file: File\)/);
  assert.match(idStore, /fun releaseTransientFile\(file: File\)/);
  assert.match(photoStore, /fun deletePrepared\(file: File\)/);
  assert.match(idStore, /fun deletePrepared\(file: File\)/);
  const releaseStart = main.indexOf("private fun releaseStudioUploadFiles()");
  const releaseEnd = main.indexOf("private fun clearCustomerUploadPdfPreviews()", releaseStart);
  assert.ok(releaseStart >= 0 && releaseEnd > releaseStart);
  const release = main.slice(releaseStart, releaseEnd);
  assert.doesNotMatch(release, /deletePrepared|sourceFile|metadataFile/,
    "Upload cleanup must not delete either encrypted Studio library entry.");
});

test("owner quote edits are sent through the existing print-job API", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  assert.match(app, /updateShopPrintJobSettings\(tokens\.accessToken,job\.id,settings\)/);
  assert.match(api, /\/api\/v1\/shop\/print-jobs\/\$\{jobId\}\/settings/);
  assert.match(app, /Revised quote .* sent to the customer for approval/);
  assert.match(app, /setPrintSettingsByJobId\(current=>/);
  assert.match(app, /copies:candidate\.copies/);
  assert.match(app, /paperSize,/);
  assert.match(app, /orientation,/);
});

test("paid jobs are staged while a physical printer is offline and checked at dispatch", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const claimStart = program.indexOf('agentApi.MapPost("/jobs/claim"');
  const claimEnd = program.indexOf('agentApi.MapGet("/jobs/{jobId:guid}/document"', claimStart);
  const dispatchStart = program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/dispatch"');
  const dispatchEnd = program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/retry-pdf-export"', dispatchStart);
  assert.ok(claimStart >= 0 && claimEnd > claimStart);
  assert.ok(dispatchStart >= 0 && dispatchEnd > dispatchStart);
  const claimRoute = program.slice(claimStart, claimEnd);
  const dispatchRoute = program.slice(dispatchStart, dispatchEnd);
  assert.match(claimRoute, /printer\.IsActive/);
  assert.match(claimRoute, /printer\.QueueName != null/);
  assert.doesNotMatch(claimRoute, /printer\.Status == "Ready"/);
  assert.doesNotMatch(claimRoute, /printer\.LastSeenUtc/);
  assert.match(dispatchRoute, /printer\.Status == "Ready"/);
  assert.match(dispatchRoute, /printer\.LastSeenUtc/);
});

test("plan-limited HTTPS fallback is only offered when the Agent is unavailable", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  assert.match(app, /secureQueueFallbackEnabled = cloudAssistedPrintingEnabled \|\| !localAgent\?\.endpoints\.length \|\| localFallbackSuggested/);
  const directUploadStart = api.indexOf("export async function uploadCustomerDocument(");
  const directUploadEnd = api.indexOf("type CustomerLocalAgentApi", directUploadStart);
  const directUpload = api.slice(directUploadStart, directUploadEnd);
  assert.doesNotMatch(directUpload, /X-Print-Queue-Fallback/);
  const publicUploadStart = program.indexOf('publicApi.MapPost("/sessions/current/jobs"');
  const publicUploadEnd = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"', publicUploadStart);
  assert.ok(publicUploadStart >= 0 && publicUploadEnd > publicUploadStart);
  const publicUpload = program.slice(publicUploadStart, publicUploadEnd);
  assert.match(publicUpload, /!subscription\.CloudAssistedPrintingEnabled && localAgentAvailable/);
  assert.doesNotMatch(publicUpload, /queueFallbackRequested/);
  assert.doesNotMatch(program, /X-Print-Queue-Fallback|queueFallbackRequested/);
  assert.doesNotMatch(api, /X-Print-Queue-Fallback|allowQueueFallback/);
  assert.match(app, /setCheckStatus\("unavailable"\);\s*onReady\(false\);[\s\S]*?onUnavailable\(\);/);
  assert.match(app, /const agentHeartbeatOffline=readiness\?\.heartbeatFresh!==true;[\s\S]*?if\(cloudAssistedPrintingEnabled\|\|agentHeartbeatOffline\)onUnavailable\(\)/);
});

test("pre-submission printer capability blocks are explained in the shop queue", () => {
  const app = read("../src/App.tsx");
  assert.match(app, /function printBlockMessage\(code:string\):string/);
  assert.match(app, /case "PAPER_SIZE_UNSUPPORTED"/);
  assert.match(app, /case "COLOUR_UNSUPPORTED"/);
  assert.match(app, /case "DUPLEX_UNSUPPORTED"/);
  assert.match(app, /job\.status==="LOCAL_QUEUE_READY"&&job\.failureCode/);
  assert.match(app, /Reassign this batch to a compatible printer/);
  assert.match(app, /explicitly approved before output/);
});

test("a pre-output capability block clears the attempted spooler name so the existing job can be reassigned", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const blockedStart = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/print-blocked"');
  const blockedEnd = program.indexOf('agentApi.MapPost("/jobs/{jobId:guid}/output-unconfirmed"', blockedStart);
  const assignmentStart = program.indexOf('shop.MapPatch("/print-jobs/{jobId:guid}/printer"');
  const assignmentEnd = program.indexOf('shop.MapPatch("/print-jobs/{jobId:guid}/settings"', assignmentStart);
  assert.ok(blockedStart >= 0 && blockedEnd > blockedStart);
  assert.ok(assignmentStart >= 0 && assignmentEnd > assignmentStart);
  const blocked = program.slice(blockedStart, blockedEnd);
  const assignment = program.slice(assignmentStart, assignmentEnd);
  assert.match(blocked, /job\.Status = "LOCAL_QUEUE_READY"/);
  assert.match(blocked, /job\.SpoolerJobName = null/);
  assert.match(blocked, /job\.SpoolerJobId = null/);
  assert.match(blocked, /job\.SpoolerStatus = "BLOCKED_BEFORE_SUBMISSION"/);
  assert.match(assignment, /var readyQueueCanBeReassigned = selectedJob\.Status == "LOCAL_QUEUE_READY"/);
  assert.match(assignment, /string\.IsNullOrWhiteSpace\(selectedJob\.SpoolerJobName\)/);
  assert.match(assignment, /needsQueueReset/);
});

test("reassigning a blocked queued job clears the previous printer failure", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const resetStart = program.indexOf("if (needsQueueReset)");
  const failureReset = program.indexOf("batchJob.FailureCode = null;", resetStart);
  const spoolerReset = program.indexOf("batchJob.SpoolerStatus = null;", resetStart);
  assert.ok(resetStart >= 0 && failureReset > resetStart && spoolerReset > failureReset);
});

test("cloud upload retries must match the original file, batch and selected print settings", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const routeStart = program.indexOf('publicApi.MapPost("/sessions/current/jobs"');
  const routeEnd = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"', routeStart);
  assert.ok(routeStart >= 0 && routeEnd > routeStart);
  const route = program.slice(routeStart, routeEnd);
  const formRead = route.indexOf("await context.Request.ReadFormAsync(ct)");
  const existingLookup = route.indexOf("var existing = await db.PrintJobs.AsNoTracking()");
  const batchPaymentGate = route.indexOf("var batchCheckoutLocked =");
  assert.ok(formRead >= 0 && existingLookup > formRead);
  assert.ok(batchPaymentGate > existingLookup);
  assert.match(route, /SHA256\.HashDataAsync\(existingInput, ct\)/);
  for (const setting of ["Copies", "ColourMode", "PaperSize", "IsDuplex", "Orientation"])
    assert.ok(route.includes("existing."+setting), `Retry must compare ${setting}.`);
  assert.match(route, /existingDocument\.Sha256, suppliedSha/);
  assert.match(route, /existingDocument\.OriginalFileName != originalName/);
  assert.match(route, /existingDocument\.SizeBytes != file\.Length/);
});

test("HTTPS recovery returns the original saved print settings on new and duplicate responses", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const recoveryStart = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery"');
  const recoveryEnd = program.indexOf('publicApi.MapPost("/jobs/{jobId:guid}/quote"', recoveryStart);
  assert.ok(recoveryStart >= 0 && recoveryEnd > recoveryStart);
  const recovery = program.slice(recoveryStart, recoveryEnd);
  for (const entity of ["preflightJob", "existingJob", "job"]) {
    for (const property of ["ColourMode", "PaperSize", "IsDuplex", "Orientation", "PricingVersion"])
      assert.ok(recovery.includes(`${entity}.${property}`), `Recovery must return ${entity}.${property}.`);
  }
});

test("local Agent completion creates the job with the customer's ticket settings", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const completeStart = program.indexOf('agentApi.MapPost("/local-uploads/{ticketId:guid}/complete"');
  const jobStart = program.indexOf("var job = new PrintJob", completeStart);
  const documentStart = program.indexOf("var printDocument = new PrintDocument", jobStart);
  assert.ok(completeStart >= 0 && jobStart > completeStart && documentStart > jobStart);
  const localJob = program.slice(jobStart, documentStart);
  assert.match(localJob, /Copies = ticket\.Copies/);
  assert.match(localJob, /ColourMode = ticket\.ColourMode/);
  assert.match(localJob, /PaperSize = ticket\.PaperSize/);
  assert.match(localJob, /IsDuplex = ticket\.IsDuplex/);
  assert.match(localJob, /Orientation = ticket\.Orientation/);
});

test("local upload ticket retries preserve batch identity and renew the same expired reservation", () => {
  const program = read("../../../src/SmartPrint.Api/Program.cs");
  const api = read("../src/api.ts");
  const app = read("../src/App.tsx");
  const entitlements = read("../../../src/SmartPrint.Api/Services/TenantEntitlementService.cs");
  const ticketStart = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets"');
  const ticketEnd = program.indexOf('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery"', ticketStart);
  assert.ok(ticketStart >= 0 && ticketEnd > ticketStart);
  const ticketRoute = program.slice(ticketStart, ticketEnd);
  assert.match(ticketRoute, /request\.BatchId is Guid requestedBatchId && existing\.BatchId != requestedBatchId/);
  assert.match(ticketRoute, /requestedBatchName is not null && existing\.BatchName != requestedBatchName/);
  assert.match(ticketRoute, /request\.BatchId is null \|\| racedTicket\.BatchId == request\.BatchId\.Value/);
  assert.match(ticketRoute, /requestedBatchName is null \|\| racedTicket\.BatchName == requestedBatchName/);
  assert.match(ticketRoute, /LocalUploadRecoveryRules\.CanRenewExpiredTicket/);
  assert.match(ticketRoute, /existing = lockedTicket/);
  assert.match(ticketRoute, /lockedTicket\.GrantHash == originalTicket\.GrantHash/);
  assert.match(ticketRoute, /lockedTicket\.BatchId == originalTicket\.BatchId/);
  assert.match(ticketRoute, /lockedTicket\.BatchGroupName == originalTicket\.BatchGroupName/);
  assert.match(ticketRoute, /FOR UPDATE/);
  assert.match(ticketRoute, /lockedTicket\.ExpiresUtc = renewalNow\.AddMinutes\(15\)/);
  assert.match(ticketRoute, /station\.PrinterDeviceId == lockedTicket\.PrinterDeviceId/);
  assert.match(ticketRoute, /printer\.AgentDeviceId == lockedTicket\.AgentDeviceId/);
  assert.match(ticketRoute, /agent\.LastSeenUtc >= renewalNow\.AddMinutes\(-2\)/);

  const expiredStart = api.indexOf("if (isUncompletedLocalUploadTicketExpired(ticket.completed, ticket.expiresUtc))");
  const expiredEnd = api.indexOf("if (ticket.completed)", expiredStart);
  assert.ok(expiredStart >= 0 && expiredEnd > expiredStart);
  const expiredRecovery = api.slice(expiredStart, expiredEnd);
  assert.match(expiredRecovery, /uploadCustomerLocalTicketViaCloud/);
  assert.match(expiredRecovery, /exception\.code === "local_upload_recovery_expired"/);
  assert.match(expiredRecovery, /exception\.code === "batch_upload_locked_after_payment_start"/);
  assert.match(expiredRecovery, /clearCustomerLocalUploadAttempt/);
  assert.match(expiredRecovery, /LocalUploadOutcomeUnknownError/);
  assert.match(app, /batchCheckoutLocked=exception instanceof LocalUploadPreflightError/);
  assert.match(app, /!batchCheckoutLocked&&canFallbackToCloudFromLocalFailure/);
  assert.match(app, /Checkout has already started for this batch/);
  assert.match(entitlements, /!db\.PrintBatchPayments\.Any\(payment =>[\s\S]*?payment\.Status != "REJECTED"/);
});


test("customer local transfer remains visible with a clear unavailable state when no Agent endpoint exists",()=>{
 const app=read("../src/App.tsx");
 const start=app.indexOf("function CustomerLocalConnection("),end=app.indexOf("type CustomerGroupSettings",start);
 const connection=app.slice(start,end);
 assert.match(connection,/if\(!localAgent\?\.endpoints\.length\)[\s\S]*?setCheckStatus\("unavailable"\)/);
 assert.doesNotMatch(connection,/if\(!localAgent\?\.endpoints\.length\)return null/);
 assert.match(connection,/Direct transfer is temporarily unavailable/);
 assert.match(app,/customer-upload-mode-unavailable/);
});

test("owner preview identifies actual supported file content instead of trusting MIME headers",()=>{
 const app=read("../src/App.tsx");
 const mediaPreview=read("../src/ShopDocumentPreviewSurface.tsx");
 assert.match(app,/identifyDocumentPreviewMediaType\(blob\)/);
 assert.match(mediaPreview,/<strong>Preview unavailable<\/strong>/);
 assert.doesNotMatch(app,/const storedType=\(job\.document\.mediaType\|\|""\)/);
});

test("customer web upload accepts drag and drop and contains no JavaScript browser alert dialogs",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/onDragOver=\{event=>\{if\(!batchLockedForPayment\)event\.preventDefault\(\);\}\}/);
 assert.match(app,/onDrop=\{event=>\{event\.preventDefault\(\);if\(batchLockedForPayment\)return;(?:void\s+)?chooseFiles\(Array\.from\(event\.dataTransfer\.files\|\|\[\]\)\);\}\}/);
 assert.doesNotMatch(app,/window\.(alert|confirm|prompt)\s*\(/);
});

test("photo studio's selected photo quantity creates one multi-page file with one photo-count record",()=>{
 const app=read("../src/App.tsx");
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/const pages\s*=\s*Math\.ceil\(parsedCopies\s*\/\s*outputPhotosPerPage\)/);
 assert.match(studio,/onApply\(\[\{[\s\S]*?file:\s*output,[\s\S]*?photoCount:\s*parsedCopies,[\s\S]*?photoOutputLayout,[\s\S]*?photoPaperFinish:[\s\S]*?previewPages:\s*jpegPages\.map/);
 assert.match(studio,/buildPhotoPdfBlob\(jpegPages, photoOutputLayout, isPhotoPaperLayout \? size : undefined\)/);
 assert.match(studio,/export type PreparedPhotoSheet = \{/);
 assert.match(studio,/resolutionDpi\?: number/);
 assert.match(studio,/editorSettings\?: PhotoStudioEditorSettings/);
 assert.match(app,/const generated=sheets\.map\(sheet=>sheet\.file\)/);
 assert.match(app,/photos\[fp\]=\{sizeCode:sheet\.sizeCode,photoCount:sheet\.photoCount,photosPerPage:sheet\.photosPerPage,photoOutputLayout:sheet\.photoOutputLayout,photoPaperFinish:sheet\.photoPaperFinish\}/);
});


test("Shop Wi-Fi option remains visible when HTTPS fallback is disabled",()=>{
 const app=read("../src/App.tsx");
 const start=app.indexOf('<div className="customer-upload-modes">');
 const picker=app.indexOf('<div className="customer-upload-picker-grid"',start);
 assert.ok(start>=0&&picker>start);
 const modes=app.slice(start,picker);
 assert.match(modes,/customer-upload-mode-unavailable/);
 assert.match(modes,/secureQueueFallbackEnabled&&/);
 assert.doesNotMatch(modes,/\? <div className="customer-upload-modes"/);
});

 
test("cloud shop document preview retries the binary fetch with a refreshed access token",()=>{
 const api=read("../src/api.ts");
 const start=api.indexOf("export async function downloadShopDocument(");
 const end=api.indexOf("export function createAgentEnrollmentCode(",start);
 assert.ok(start>=0&&end>start);
 const preview=api.slice(start,end);
 assert.match(preview,/if \(response\.status === 401\)\s*\{\s*const refreshed = await refreshStoredSession\(token\)/);
 assert.match(preview,/if \(refreshed\) response = await request\(refreshed\.accessToken\)/);
 assert.match(preview,/Your shop session has expired/);
});

test("Photo Studio uses only the uploaded source and offers no edited-photo export action",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/source:\s*File/);
 assert.doesNotMatch(studio,/<input[^>]+type=["']file/i);
 assert.doesNotMatch(studio,/<a[^>]+download/i);
 assert.match(studio,/const protect = mode !== "private-library"/);
 assert.match(studio,/setDesktopContentProtection\(protect\)/);
 assert.match(studio,/previewProtection/);
 assert.match(studio,/const actionLabel = mode === "private-library" \? "Save private sheet"/);
 const studioCss=read("../src/PhotoStudio.module.css");
 assert.match(studioCss,/\.previewProtection::before/);
 assert.match(studio,/mode === "private-library" \? "Save private sheet"/);
 assert.match(studio,/mode === "shop-create" \? "Create photo order"/);
 assert.match(studio,/mode === "shop" \? "Save sheet to this job"/);
 assert.match(studio,/: "Prepare A4 sheet"/);
 assert.match(studio,/Prepared photo output replaces this upload; original stays untouched/);
 assert.match(studio,/Replace the unpaid job photo PDF; approval and dispatch still apply/);
 assert.match(studio,/type="date"/);
 assert.match(studio,/formatDobForPrint/);
});

test("photo quantities support up to 100 copies and automatically paginate over A4 sheets",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/const MAX_PHOTO_COUNT = UPLOAD_LIMITS\.maxPhotoCount/);
 assert.match(studio,/const maxCopies=MAX_PHOTO_COUNT/);
 assert.match(studio,/const sheetCount = Math\.ceil\(copies \/ photosPerPage\)/);
 assert.match(studio,/Photos per A4 page/);
 assert.match(studio,/Final A4 page:/);
});

 
test("cloud shop photo revisions require an unpaid review-stage image and validate every A4 page",()=>{
 const program=read("../../../src/SmartPrint.Api/Program.cs");
 const start=program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/photo-revision"');
 const end=program.indexOf('shop.MapGet("/print-jobs/{jobId:guid}/preview-source"',start);
 assert.ok(start>=0&&end>start);
 const route=program.slice(start,end);
 assert.match(route,/IsShopOwner\(principal\)/);
 assert.match(route,/job\.Status != "AWAITING_SHOP_APPROVAL"/);
 assert.match(route,/document\.StorageMode, "CLOUD"/);
 assert.match(route,/var photoSourceMediaType = documentSnapshot\.PhotoSourceMediaType \?\? documentSnapshot\.MediaType/);
 assert.match(route,/photoSourceMediaType is not \("image\/jpeg" or "image\/png"\)/);
 assert.match(route,/if \(jobPreflight\.PrivatePrinting\)/);
 assert.match(route,/hasPayment \|\| hasBatchPayment/);
 assert.match(route,/job\.LocalQueueReceivedUtc is not null/);
 assert.match(route,/job\.ClaimedByAgentDeviceId is not null/);
 assert.match(route,/PricingCalculator\.MaximumPhotosPerA4Sheet/);
 assert.match(route,/PricingCalculator\.PhotoPageCount\(photoCount, photoSizeCode, photosPerPage\)/);
 assert.match(route,/stored\.Analysis\.PageCount != expectedPageCount/);
 assert.match(route,/SHOP_PHOTO_ENHANCED/);
});

test("customer photo previews show the source image and branded prepared pages without a download link",()=>{
 const app=read("../src/App.tsx");
 const previewStart=app.indexOf("{previewUrl&&file&&");
 const previewEnd=app.indexOf("{files.length>0&&<button className=\"customer-flow-button\"",previewStart);
 assert.ok(previewStart>=0&&previewEnd>previewStart);
 const preview=app.slice(previewStart,previewEnd);
 assert.match(preview,/customer-original-image-preview/);
 assert.match(preview,/customer-edited-photo-frame/);
 assert.match(preview,/SmartPrint by QuantaEdge/);
 assert.doesNotMatch(preview,/<a[^>]+download=/i);
 assert.doesNotMatch(preview,/photoSettingsByFileFingerprint\\[[^\\]]+\\]\\?<iframe/);
});

test("customer Photo Studio uploads the prepared PDF, not a second raw-photo copy or editor-settings payload",()=>{
 const app=read("../src/App.tsx");
 const start=app.indexOf("function applyPhotoSheets(");
 const end=app.indexOf("function openPhotoStudio(",start);
 assert.ok(start>=0&&end>start);
 const flow=app.slice(start,end);
 assert.match(flow,/const generated=sheets\.map\(sheet=>sheet\.file\),nextFiles=\[\.\.\.files\.slice\(0,index\),\.\.\.generated,\.\.\.files\.slice\(index\+1\)\]/);
 assert.match(flow,/delete photos\[photoStudioSourceFingerprint\]/);
 assert.match(flow,/photos\[fp\]=\{sizeCode:sheet\.sizeCode,photoCount:sheet\.photoCount,photosPerPage:sheet\.photosPerPage,photoOutputLayout:sheet\.photoOutputLayout,photoPaperFinish:sheet\.photoPaperFinish\}/);
 assert.match(flow,/setPhotoStudioSource\(null\)/);
 assert.doesNotMatch(flow,/editorSettings|textLayers/);
});

test("the exact same Photo Studio is opened from the shop image review",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/openShopPhotoStudioForJob\(preview\.job,preview\)/);
 assert.match(app,/setShopPhotoStudio\(\{source,job,initialSettings,returnPreview\}\)/);
 assert.match(app,/if\(editor\.job\.privatePrinting \|\| !editor\.job\.photoSourceAvailable\)/);
 assert.match(app,/replaceShopPhotoDocument\(tokens\.accessToken,editor\.job\.id,sheet\.file,sheet\.sizeCode,sheet\.photoCount,sheet\.photosPerPage,JSON\.stringify\(sheet\.editorSettings\?\?\{\}\),sheet\.photoOutputLayout,sheet\.photoPaperFinish\)/);
 assert.match(app,/shopPhotoStudio && <PhotoStudio source=\{shopPhotoStudio\.source\}/);
 assert.match(app,/Enhance photo for printing/);
});

test("native Windows content capture protection covers the photo editor and image preview",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 const app=read("../src/App.tsx");
 const bridge=read("../src/desktop-bridge.ts");
 const permissions=JSON.parse(read("../../desktop/src-tauri/capabilities/default.json"));
 assert.match(studio,/const protect = mode !== "private-library"/);
 assert.match(studio,/setDesktopContentProtection\(protect\)/);
 assert.match(studio,/previewProtection/);
 assert.match(app,/setDesktopContentProtection\(true\)/);
 assert.match(bridge,/getCurrentWindow\(\)\.setContentProtected\(enabled\)/);
 assert.ok(permissions.permissions.includes("core:window:allow-set-content-protected"));
});

 
test("shop owner can open the same editor for cloud and local Agent images",()=>{
 const app=read("../src/App.tsx");
 const api=read("../src/api.ts");
 assert.match(app,/preview\.job\.storageMode==="CLOUD"\|\|preview\.job\.storageMode==="LOCAL_AGENT"/);
 assert.match(app,/editor\.job\.storageMode==="LOCAL_AGENT"[\s\S]*?replaceShopPhotoDocumentLocally/);
 assert.match(app,/replaceShopPhotoDocument\(tokens\.accessToken,editor\.job\.id,sheet\.file,sheet\.sizeCode,sheet\.photoCount,sheet\.photosPerPage,JSON\.stringify\(sheet\.editorSettings\?\?\{\}\),sheet\.photoOutputLayout,sheet\.photoPaperFinish\)/);
 assert.match(api,/\/api\/v1\/shop\/print-jobs\/\$\{jobId\}\/photo-revision-grant/);
 assert.match(api,/\/api\/v1\/local\/documents\/\$\{encodeURIComponent\(jobId\)\}\/photo-revision/);
});

test("local photo revisions use a purpose-bound grant and preserve local storage",()=>{
 const program=read("../../../src/SmartPrint.Api/Program.cs");
 const agent=read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
 const queue=read("../../../src/SmartPrint.Agent/AgentLocalQueueStore.cs");
 const grant=read("../../../src/SmartPrint.Api/Security/LocalDocumentPhotoRevisionGrant.cs");
 assert.match(program,/LocalDocumentPhotoRevisionGrant\.Issue\([\s\S]*?GetActorId\(principal\)/);
 assert.match(program,/LocalDocumentPhotoRevisionGrant\.TryValidate/);
 assert.match(program,/document\.StorageMode != "LOCAL_AGENT" \|\| document\.AgentDeviceId != device\.Id/);
 assert.match(program,/document\.StorageMode = previousStorageMode/);
 assert.match(program,/ActorId = grant\.ActorUserId/);
 assert.match(agent,/X-Print-Photo-Revision-Grant/);
 const localRevisionStart=agent.indexOf('app.MapPost("/api/v1/local/documents/{jobId:guid}/photo-revision"');
 const privateGuard=agent.indexOf("if (entry?.PrivatePrinting == true)",localRevisionStart);
 const formRead=agent.indexOf("context.Request.ReadFormAsync(ct)",localRevisionStart);
 assert.ok(localRevisionStart>=0&&privateGuard>localRevisionStart&&formRead>privateGuard,
   "Private Printing must be rejected before the local Agent reads the prepared PDF or editor-settings form payload.");
 assert.match(agent,/ReplaceStagedPhotoDocumentAsync/);
 assert.match(agent,/local-document-photo-revisions\/\{jobId\}\/complete/);
 assert.match(queue,/await commitServerRevisionAsync\(\)/);
 assert.match(queue,/File\.Move\(backupPath, entry\.DocumentPath\)/);
 assert.match(grant,/local_document_photo_revision/);
});

test("local photo revision rejects paid, handed-off and changed originals",()=>{
 const program=read("../../../src/SmartPrint.Api/Program.cs");
 const start=program.indexOf('agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/complete"');
 const end=program.indexOf('agentApi.MapGet("/local-document-previews/{jobId:guid}/authorize"',start);
 assert.ok(start>=0&&end>start);
 const route=program.slice(start,end);
 assert.match(route,/job\.Status != "AWAITING_SHOP_APPROVAL"/);
 assert.match(route,/job\.LocalQueueReceivedUtc is not null/);
 assert.match(route,/job\.ClaimedByAgentDeviceId is not null/);
 assert.match(route,/document\.StorageMode != "LOCAL_AGENT"/);
 assert.match(route,/document\.AgentDeviceId != device\.Id/);
 assert.match(route,/hasPayment \|\| hasBatchPayment/);
 assert.match(route,/retainedSourceSha256, request\.ExpectedOriginalSha256/);
 assert.match(route,/retainedSourceSizeBytes != request\.ExpectedOriginalSizeBytes/);
 assert.match(route,/request\.PhotosPerPage > PricingCalculator\.MaximumPhotosPerA4Sheet/);
 assert.match(route,/request\.PageCount != PricingCalculator\.PhotoPageCount\(request\.PhotoCount, request\.PhotoSizeCode, request\.PhotosPerPage\)/);
});


test("shop-owner Photo Studio supports cloud and local storage without changing a local job into cloud storage",()=>{
 const app=read("../src/App.tsx");
 const api=read("../src/api.ts");
 const program=read("../../../src/SmartPrint.Api/Program.cs");
 const agent=read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
 assert.match(app,/preview\.job\.storageMode==="CLOUD"\|\|preview\.job\.storageMode==="LOCAL_AGENT"/);
 assert.match(app,/editor\.job\.storageMode==="LOCAL_AGENT"[\s\S]*?replaceShopPhotoDocumentLocally/);
 assert.match(api,/\/api\/v1\/shop\/print-jobs\/\$\{jobId\}\/photo-revision-grant/);
 assert.match(api,/\/api\/v1\/local\/documents\/\$\{encodeURIComponent\(jobId\)\}\/photo-revision/);
 assert.match(program,/document\.StorageMode = previousStorageMode/);
 assert.match(program,/document\.AgentDeviceId = device\.Id/);
 assert.match(agent,/ReplaceStagedPhotoDocumentAsync/);
});



test("native Android protects Photo Studio from screenshots through the origin-pinned WebView bridge",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 const activity=read("../../android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
 assert.match(studio,/mode === "private-library"/);
 assert.match(studio,/postMessage\("setSensitivePreview\|on"\)/);
 assert.match(studio,/postMessage\("setSensitivePreview\|off"\)/);
 assert.match(activity,/sourceOrigin\.scheme == trustedPage\.scheme/);
 assert.match(activity,/sourceOrigin\.host\.equals\(trustedPage\.host, true\)/);
 assert.match(activity,/isMainFrame/);
 assert.match(activity,/parts\[0\] == "setSensitivePreview"/);
 assert.match(activity,/photoStudioCaptureAllowed/);
 assert.match(activity,/privateStudioCaptureBlocked/);
 assert.match(activity,/idCaptureBlocked = idStudioScreenOpen/);
 assert.match(activity,/window\.addFlags\(WindowManager\.LayoutParams\.FLAG_SECURE\)/);
 assert.match(activity,/window\.clearFlags\(WindowManager\.LayoutParams\.FLAG_SECURE\)/);
});

test("a completed customer order offers a fresh session instead of another payment or quote",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/const startNewSession=useCallback\(async\(\)=>/);
 assert.match(app,/key=\{result\.sessionToken\}/);
 assert.match(app,/isCustomerBatchFinished/);
 assert.match(app,/canStartNewSession&&<div className="customer-session-complete">/);
 assert.match(app,/onClick=\{onStartNewSession\}/);
});

test("native customer account identity accompanies QR session renewal",()=>{
 const api=read("../src/api.ts");
 const start=api.indexOf("export async function renewCustomerPrintSession(");
 const end=api.indexOf("\n}",start);
 assert.ok(start>=0&&end>start);
 const renewal=api.slice(start,end);
 assert.match(renewal,/headers: \{ "X-Print-Session": sessionToken, \.\.\.await nativeCustomerAuthorizationHeaders\(\) \}/);
});


test("local shop Photo Studio revision completion commits the exact authorized price snapshot",()=>{
  const agent=read("../../../src/SmartPrint.Agent/LocalAgentApiHostedService.cs");
  const agentModels=read("../../../src/SmartPrint.Agent/AgentQueueModels.cs");
  const program=read("../../../src/SmartPrint.Api/Program.cs");
  assert.match(agent,/ExpectedQuotedAmountMinor = authorization\.QuotedAmountMinor/);
  assert.match(agent,/ExpectedPricingVersion = authorization\.PricingVersion/);
  assert.match(agent,/JsonContent\.Create\(completionRequest\)/);
  assert.match(agentModels,/long\? ExpectedQuotedAmountMinor = null/);
  assert.match(agentModels,/int\? ExpectedPricingVersion = null/);
  const start=program.indexOf('agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/complete"');
  const end=program.indexOf("\nagentApi.",start+30);
  assert.ok(start>=0&&end>start);
  const complete=program.slice(start,end);
  assert.match(complete,/expectedQuotedAmountMinor <= 0/);
  assert.match(complete,/pricing\.Version != expectedPricingVersion \|\| amount != expectedQuotedAmountMinor/);
  assert.match(complete,/photo_revision_pricing_changed/);
  assert.match(complete,/job\.QuotedAmountMinor == request\.ExpectedQuotedAmountMinor/);
  assert.match(complete,/job\.PricingVersion == request\.ExpectedPricingVersion/);
});

test("local shop Photo Studio revision authorization and completion enforce the selected photo raster contract",()=>{
  const program=read("../../../src/SmartPrint.Api/Program.cs");
  const authorizeStart=program.indexOf('agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/authorize"');
  const authorizeEnd=program.indexOf('agentApi.MapPost("/local-document-photo-revisions/{jobId:guid}/complete"',authorizeStart);
  const completeStart=authorizeEnd;
  const completeEnd=program.indexOf("\nagentApi.MapPost(",completeStart+30);
  assert.ok(authorizeStart>=0&&authorizeEnd>authorizeStart);
  assert.ok(completeStart>=0&&completeEnd>completeStart);
  const authorize=program.slice(authorizeStart,authorizeEnd);
  const complete=program.slice(completeStart,completeEnd);
  assert.match(authorize,/request\.PageSizesJson\.Length > 64_000/);
  assert.match(authorize,/PhotoSheetPdfRules\.HasExactlyPhotoPages\(request\.PageSizesJson, request\.PageCount, request\.PhotoOutputLayout, request\.PhotoSizeCode\)/);
  assert.match(complete,/PhotoSheetPdfRules\.HasExactlyPhotoPages\(request\.PageSizesJson, request\.PageCount, request\.PhotoOutputLayout, request\.PhotoSizeCode\)/);
  assert.match(authorize,/photo_sheet_layout_invalid/);
  assert.match(complete,/photo_sheet_layout_invalid/);
});

test("cloud shop Photo Studio revisions validate complete A4 raster geometry before replacing the saved job",()=>{
  const program=read("../../../src/SmartPrint.Api/Program.cs");
  const start=program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/photo-revision"');
  const end=program.indexOf('shop.MapGet("/print-jobs/{jobId:guid}/preview-source"',start);
  assert.ok(start>=0&&end>start);
  const endpoint=program.slice(start,end);
  const validation=endpoint.indexOf("PhotoSheetPdfRules.HasExactlyPhotoPages(stored.Analysis.PageSizesJson, expectedPageCount, photoOutputLayout, photoSizeCode)");
  const replacement=endpoint.indexOf("document.StorageKey = stored.StorageKey");
  assert.match(endpoint,/string\.Equals\(stored\.Analysis\.MediaType, "application\/pdf"/);
  assert.ok(validation>=0&&replacement>validation,
    "The new PDF's A4 pages and full-page photo raster must be verified before replacing the existing document.");
  assert.match(endpoint,/await storage\.DeleteAsync\(stored\.StorageKey\);[\s\S]*?await transaction\.RollbackAsync\(ct\)/);
  assert.match(endpoint,/The replacement Photo Studio PDF must match the selected photo-paper dimensions and output layout/);
});

test("shop-originated photo orders use their own tenant-owned job and the shared Photo Studio",()=>{
 const app=read("../src/App.tsx");
 const studio=read("../src/PhotoStudio.tsx");
 const api=read("../src/api.ts");
 const program=read("../../../src/SmartPrint.Api/Program.cs");
 const endpointStart=program.indexOf('shop.MapPost("/photo-jobs"');
 const endpointEnd=program.indexOf('shop.MapGet("/reports/summary"',endpointStart);
 assert.ok(endpointStart>=0&&endpointEnd>endpointStart);
 const endpoint=program.slice(endpointStart,endpointEnd);
 assert.match(app,/New photo order/);
 assert.match(app,/ShopPhotoOrderModal/);
 assert.match(app,/mode="shop-create"/);
 assert.match(studio,/"SHOP · NEW PHOTO ORDER"/);
 assert.match(api,/createShopPhotoJob/);
 assert.match(endpoint,/if \(!IsShopOwner\(principal\) \|\| !TryGetTenantId\(principal, out var tenantId\)\)/);
 assert.match(endpoint,/Origin = "SHOP_OWNER"/);
 assert.match(endpoint,/QrStationId = null/);
 assert.match(endpoint,/CustomerSessionId = null/);
 assert.match(endpoint,/Status = paymentMethod == "UPI" \? "AWAITING_CUSTOMER_PAYMENT" : "AWAITING_COUNTER_PAYMENT"/);
 assert.match(endpoint,/new PrintPayment/);
 assert.match(endpoint,/PhotoSheetPdfRules\.HasExactlyPhotoPages/);
 assert.match(endpoint,/item\.IdempotencyKey == idempotencyKey/);
 assert.match(endpoint,/session\.TenantId == tenantId && session\.CustomerUserId == user\.Id/);
 assert.match(endpoint,/SHOP_PHOTO_ORDER_CREATED_AWAITING_PAYMENT/);
 assert.match(api,/form\.append\("paymentMethod", values\.paymentMethod\)/);
 assert.match(app,/buildCustomerUpiIntent\(/);
});


test("customer uploader keeps its batch example, inline Android PDF preview and real status transitions",()=>{
 const app=read("../src/App.tsx");
 const api=read("../src/api.ts");
 const program=read("../../../src/SmartPrint.Api/Program.cs");
 const css=read("../src/styles.css");
 assert.match(app,/placeholder="e.g. Manjeet - College Notes"/);
 assert.doesNotMatch(app,/Rahul\s*[–-]\s*College Notes/);
 assert.match(app,/<CustomerNativePdfPreview[\s\S]*onPage=/);
 assert.match(app,/event\.previousStatus!==event\.newStatus/);
 assert.match(app,/customerStatusTone\(jobStatus\.status\)/);
 assert.match(app,/Last updated · \{formatIstDateTime\(jobStatus\.updatedUtc\)\}/);
 assert.match(api,/previousStatus\?: string \| null/);
 assert.match(program,/new \{ item\.PreviousStatus, item\.NewStatus, item\.ActorType, item\.CreatedUtc \}/);
 assert.match(css,/\.customer-native-pdf-preview/);
 assert.match(css,/\.customer-batch-quote-card>div:first-child>strong\{[\s\S]*?white-space:nowrap/);
});

test("Android customer PDF preview stays inline and UPI QR downloads through the Android Save As picker",()=>{
 const activity=read("../../android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
 const qr=read("../src/CustomerUpiQr.tsx");
 assert.match(activity,/parts\[0\] == "previewCustomerPdf"[\s\S]*?renderCustomerPdfPreview/);
 assert.match(activity,/PdfRenderer\(descriptor\)/);
 assert.match(activity,/customerUploadPdfPreviewSensitiveTokens/);
 assert.match(activity,/ActivityResultContracts\.CreateDocument\("image\/png"\)/);
 assert.match(activity,/parts\[0\] == "saveCustomerPaymentQr"/);
 assert.match(qr,/saveCustomerPaymentQr\|/);
 assert.match(qr,/navigator\.clipboard\?\.writeText/);
 assert.match(qr,/Copy UPI ID/);
});


test("customer-facing print choices explain privacy versus connection without changing the available modes",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/How your files reach the shop/);
 assert.match(app,/Choose a connection method\. Your privacy setting above stays the same\./);
 assert.match(app,/Direct over shop Wi-Fi · unencrypted connection/);
 assert.match(app,/Encrypted online transfer · print when ready/);
 assert.match(app,/customerStatusLabel\(status:string\):string/);
 assert.match(app,/Waiting for shop review/);
 assert.match(app,/calculateBatchQuoteBreakdown\(acceptedQuoteEntries\.map/);
 assert.match(app,/batchQuoteBreakdown\.idCardSheets/);
 assert.match(app,/batchQuoteBreakdown\.photoPages/);
 assert.match(app,/batchQuoteBreakdown\.normalDocumentSourcePages/);
 assert.match(app,/batchQuoteBreakdown\.totalOutputPagesAndSheets/);
 assert.match(app,/className="customer-batch-quote-lines"/);
 assert.match(app,/Normal documents ·/);
 assert.match(app,/ID cards ·/);
 assert.match(app,/Photo printing ·/);
 assert.match(app,/UPI transaction ID \(UTR\)/);
 assert.match(app,/Find it in your UPI app under payment details or transaction history/);
 assert.ok(app.includes("Normal PDF/document pages are original pages."));
 assert.ok(app.includes("For driver-backed N-up, the selected pages-per-sheet value groups source pages onto output sides;"));
 assert.ok(app.includes("Normal-document output counts page sides after copies."));
 assert.ok(app.includes("With duplex printing, two page sides may share one physical sheet."));
});



test("customer status keeps ticket-only ID cards out of the normal document category",()=>{
 const app=read("../src/App.tsx");
 const program=read("../../../src/SmartPrint.Api/Program.cs");
 const rules=read("../../../src/SmartPrint.Api/Domain/PrivateRelayPrintKindRules.cs");
 assert.match(app,/printKindsByJobId\[entry\.item\.job\.id\]==="ID_DOCUMENT"/);
 assert.match(program,/HasIdDocumentTicket = db\.LocalPrintUploadTickets\.AsNoTracking\(\)\.Any\(ticket =>[\s\S]*?ticket\.PrintKind == "ID_DOCUMENT"/);
 assert.match(program,/ResolveCustomerStatusPrintKind\([\s\S]*?storedJob\.HasIdDocumentTicket/);
 assert.match(rules,/hasIdDocumentTicket \? "ID_DOCUMENT" : storedPrintKind \?\? "DOCUMENT"/);
});

test("offline Photo Studio explains one-photo editing, friendly sizes, and shop print handoff",()=>{
 const entry=read("../src/PrivatePhotoStudioEntry.tsx");
 assert.ok(entry.includes("function photoSizeLabel(sizeCode:string):string"));
 assert.ok(entry.includes('"35x45":"35 × 45 mm"'));
 assert.ok(entry.includes('"25x35":"25 × 35 mm"'));
 assert.ok(entry.includes('"2x2":"2 × 2 inch"'));
 assert.ok(entry.includes('"4x6":"4 × 6 inch"'));
 assert.ok(entry.includes("{photoSizeLabel(document.sizeCode)} · {document.photoCount} photos · {document.photosPerPage} per A4 sheet"));
 assert.ok(entry.includes("Choose one original photo. Crop and enhance it, then set the number of copies and photos per A4 sheet in the editor."));
 assert.ok(entry.includes("To print a saved sheet: tap Back to Home, scan the shop's QR code, then select the sheet under Photo Studio private documents in the shop's file picker."));
});
test("UPI amount copy is numeric and QR save feedback distinguishes success from pending or cancelled",()=>{
 const qr=read("../src/CustomerUpiQr.tsx");
 const css=read("../src/styles.css");
 assert.match(qr,/amountMinor:number/);
 assert.match(qr,/copyText\(\(amountMinor\/100\)\.toFixed\(2\),"Amount"\)/);
 assert.match(qr,/statusTone==="success"\?<Check/);
 assert.match(qr,/setStatusTone\("info"\)/);
 assert.match(qr,/CUSTOMER_PAYMENT_QR_CANCELLED[\s\S]*?setStatusTone\("info"\)/);
 assert.match(css,/\.customer-upi-download-status\.success/);
 assert.match(css,/\.customer-upi-download-status\.info/);
});

test("Android customer PDF previews render automatically and receive native bridge replies", () => {
  const app = read("../src/App.tsx");
  const componentStart = app.indexOf("function CustomerNativePdfPreview");
  const componentEnd = app.indexOf("\nfunction consumeSmartPrintNativePrintKinds", componentStart);
  assert.ok(componentStart >= 0 && componentEnd > componentStart);
  const preview = app.slice(componentStart, componentEnd);
  assert.doesNotMatch(preview, /Preview PDF/);
  assert.match(preview, /Preparing print layout preview/);
  assert.match(preview, /Preview unavailable/);
  assert.match(preview, /Previous sheet/);
  assert.match(preview, /Next sheet/);
  assert.match(preview, /The original PDF is sent to the printer driver unchanged/);
  assert.match(app, /previewCustomerPdf\|\"\+token\+\"\|\"\+sheetIndex\+\"\|\"\+pagesPerSheet/);
  assert.match(app, /current\.pagesPerSheet===payload\.pagesPerSheet/);
  assert.match(app, /pagesPerSheetDirection/);
  assert.match(app, /current\.pagesPerSheetDirection===payload\.pagesPerSheetDirection/);
  assert.match(app, /bridge\.addEventListener\("message",onNativePdfPreview\)/);
  assert.match(app, /bridge\.removeEventListener!\("message",onNativePdfPreview\)/);
  assert.doesNotMatch(app, /window\.addEventListener\("message",onNativePdfPreview\)/);
  assert.match(app, /openCustomerPdfPreview\(file\);\n  \},\[file,nativeAppMode,photoSettingsByFileFingerprint,groupSettings,fileSettings\]\)/);
});


test("browser customer N-up preview is visual-only and leaves the original upload unchanged",()=>{
 const app=read("../src/App.tsx");
 const preview=read("../src/customer-nup-preview.ts");
 assert.match(app,/createCustomerNupPreview/);
 assert.match(app,/The original PDF is uploaded unchanged/);
 assert.match(app,/Previous sheet/);
 assert.match(app,/Next sheet/);
 assert.match(preview,/embedPages/);
 assert.match(preview,/pagesPerSheetDirection/);
 assert.match(preview,/resolveGridPosition/);
 for(const direction of ["RightBottom","BottomRight","LeftBottom","BottomLeft","RightTop","TopRight","LeftTop","TopLeft"])
   assert.ok(preview.includes(`case "${direction}"`),`Missing browser mapping for ${direction}`);
 assert.match(preview,/SmartPrint by QuantaEdge/);
 const android=read("../../android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
 const nativePreviewStart=android.indexOf("private fun renderCustomerPdfSheet(");
 const nativePreviewEnd=android.indexOf("private fun renderCustomerPdfPage(",nativePreviewStart);
 assert.ok(nativePreviewStart>=0 && nativePreviewEnd>nativePreviewStart);
 assert.match(android.slice(nativePreviewStart,nativePreviewEnd),/canvas\.drawText\([\s\S]*?"SmartPrint by QuantaEdge"/);
 assert.match(android.slice(nativePreviewStart,nativePreviewEnd),/when \(pagesPerSheetDirection\)/);
 for(const direction of ["RightBottom","BottomRight","LeftBottom","BottomLeft","RightTop","TopRight","LeftTop","TopLeft"])
   assert.ok(android.slice(nativePreviewStart,nativePreviewEnd).includes(`"${direction}"`),`Missing Android mapping for ${direction}`);
});
