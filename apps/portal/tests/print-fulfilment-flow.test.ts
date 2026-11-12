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
  assert.match(route, /PhotoSheetPdfRules\.HasExactlyA4PortraitPages/);
  assert.match(route, /PrintKind = requestedPrintKind/);
  assert.match(route, /PhotoSizeCode = photoSizeCode/);
  assert.match(route, /PhotoCount = photoCount \?\? 0/);
  assert.match(route, /PhotosPerPage = photosPerPage/);
  assert.match(route, /existing\.PhotoCount != \(photoCount \?\? 0\)/);
});

test("private Photo Studio vault operations are scoped to the dedicated native route", () => {
  const main = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
  assert.match(main, /privatePhotoStudioPageOpen &&[\s\S]*Uri\.parse\(webView\?\.url\.orEmpty\(\)\)\.path == "\/photo-studio"/);
  assert.match(main, /photoStudioLibraryCommand && !photoStudioRouteActive/);
});

test("after upload only the decrypted picker copy is released and the saved library document remains", () => {
  const app = read("../src/App.tsx");
  const main = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/MainActivity.kt");
  const store = read("../../../apps/android/app/src/main/java/com/quantaedge/smartprint/PhotoStudioStore.kt");
  assert.match(app, /setFiles\(\[\]\);[\s\S]*postSmartPrintNativeMessage\("releasePhotoStudioUploadFiles"\)/);
  assert.match(main, /data == "releasePhotoStudioUploadFiles"/);
  assert.match(main, /privatePhotoUploadTempFiles\.toList\(\)\.forEach\(photoStudioStore::releaseTransientFile\)/);
  assert.match(main, /privatePhotoUploadTempFiles\.clear\(\)/);
  assert.match(store, /fun releaseTransientFile\(file: File\)/);
  assert.match(store, /fun deletePrepared\(file: File\)/);
  assert.match(store, /sourceFile\(id\)\.delete\(\)/);
  const release = main.slice(main.indexOf('data == "releasePhotoStudioUploadFiles"'), main.indexOf('data == "releasePhotoStudioUploadFiles"') + 900);
  assert.doesNotMatch(release, /deletePrepared|documentFile|sourceFile|metadataFile/,
    "Upload cleanup must not delete the encrypted saved Photo Studio library entry.");
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

test("owner preview selects the persisted supported MIME type before the blob response type",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/const storedType=\(job\.document\.mediaType\|\|""\)/);
 assert.match(app,/supportedTypes\.has\(storedType\)[\s\S]*?supportedTypes\.has\(responseType\)/);
 assert.match(app,/Preview format is not supported/);
});

test("customer web upload accepts drag and drop and contains no JavaScript browser alert dialogs",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/onDragOver=\{event=>\{if\(!batchLockedForPayment\)event\.preventDefault\(\);\}\}/);
 assert.match(app,/onDrop=\{event=>\{event\.preventDefault\(\);if\(batchLockedForPayment\)return;chooseFiles\(Array\.from\(event\.dataTransfer\.files\|\|\[\]\)\);\}\}/);
 assert.doesNotMatch(app,/window\.(alert|confirm|prompt)\s*\(/);
});

test("photo studio's selected photo quantity creates one multi-page file with one photo-count record",()=>{
 const app=read("../src/App.tsx");
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/const pages=Math\.ceil\(parsedCopies\/photosPerPage\)/);
 assert.match(studio,/onApply\(\[\{file:output,sizeCode:size\.id,photoCount:parsedCopies,photosPerPage,captionHeightMm,resolutionDpi:300,editorSettings,previewPages:jpegPages\.map/);
 assert.match(studio,/buildPhotoPdf\(jpegPages\)/);
 assert.match(studio,/export type PreparedPhotoSheet = \{/);
 assert.match(studio,/resolutionDpi\?: number/);
 assert.match(studio,/editorSettings\?: PhotoStudioEditorSettings/);
 assert.match(app,/const generated=sheets\.map\(sheet=>sheet\.file\)/);
 assert.match(app,/photos\[fp\]=\{sizeCode:sheet\.sizeCode,photoCount:sheet\.photoCount,photosPerPage:sheet\.photosPerPage\}/);
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
 assert.match(studio,/This prepared multi-page A4 photo PDF replaces it in your batch/);
 assert.match(studio,/type="date"/);
 assert.match(studio,/formatDobForPrint/);
});

test("photo quantities support up to 100 copies and automatically paginate over A4 sheets",()=>{
 const studio=read("../src/PhotoStudio.tsx");
 assert.match(studio,/const MAX_PHOTO_COUNT = 100/);
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
 assert.match(route,/document\.MediaType is not \("image\/jpeg" or "image\/png"\)/);
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

test("the exact same Photo Studio is opened from the shop image review",()=>{
 const app=read("../src/App.tsx");
 assert.match(app,/setShopPhotoStudio\(\{source,job:preview\.job,returnPreview\}\)/);
 assert.match(app,/replaceShopPhotoDocument\(tokens\.accessToken,editor\.job\.id,sheet\.file,sheet\.sizeCode,sheet\.photoCount,sheet\.photosPerPage\)/);
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
 assert.match(app,/replaceShopPhotoDocument\(tokens\.accessToken,editor\.job\.id,sheet\.file,sheet\.sizeCode,sheet\.photoCount,sheet\.photosPerPage\)/);
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
 assert.match(route,/document\.Sha256, request\.ExpectedOriginalSha256/);
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


test("cloud shop Photo Studio revisions validate complete A4 raster geometry before replacing the saved job",()=>{
  const program=read("../../../src/SmartPrint.Api/Program.cs");
  const start=program.indexOf('shop.MapPost("/print-jobs/{jobId:guid}/photo-revision"');
  const end=program.indexOf('shop.MapGet("/print-jobs/{jobId:guid}/preview-source"',start);
  assert.ok(start>=0&&end>start);
  const endpoint=program.slice(start,end);
  const validation=endpoint.indexOf("PhotoSheetPdfRules.HasExactlyA4PortraitPages(stored.Analysis.PageSizesJson, expectedPageCount)");
  const replacement=endpoint.indexOf("document.StorageKey = stored.StorageKey");
  assert.match(endpoint,/string\.Equals\(stored\.Analysis\.MediaType, "application\/pdf"/);
  assert.ok(validation>=0&&replacement>validation,
    "The new PDF's A4 pages and full-page photo raster must be verified before replacing the existing document.");
  assert.match(endpoint,/await storage\.DeleteAsync\(stored\.StorageKey\);[\s\S]*?await transaction\.RollbackAsync\(ct\)/);
  assert.match(endpoint,/The replacement Photo Studio PDF must contain exactly/);
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
 assert.match(endpoint,/PhotoSheetPdfRules\.HasExactlyA4PortraitPages/);
 assert.match(endpoint,/item\.IdempotencyKey == idempotencyKey/);
 assert.match(endpoint,/session\.TenantId == tenantId && session\.CustomerUserId == user\.Id/);
 assert.match(endpoint,/SHOP_PHOTO_ORDER_CREATED_AWAITING_PAYMENT/);
 assert.match(api,/form\.append\("paymentMethod", values\.paymentMethod\)/);
 assert.match(app,/buildCustomerUpiIntent\(/);
});
