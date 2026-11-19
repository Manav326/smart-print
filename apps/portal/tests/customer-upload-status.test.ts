import assert from "node:assert/strict";
import test from "node:test";
import { resolveCustomerUploadStatusMessage } from "../src/customer-upload-status.ts";

test("empty customer flow gives a true empty state", () => {
  assert.equal(resolveCustomerUploadStatusMessage({}), "Choose documents or images to begin.");
});

test("selected files and uploaded files have distinct guidance", () => {
  assert.equal(resolveCustomerUploadStatusMessage({ filesSelected: 2 }), "Review your selected files and upload.");
  assert.equal(resolveCustomerUploadStatusMessage({ uploadedFileCount: 2 }),
    "Files uploaded successfully · Choose print settings or calculate a quote.");
});

test("byte transfer and shop processing stages remain visible", () => {
  assert.match(resolveCustomerUploadStatusMessage({
    uploadProgress: { files: [{ status: "TRANSFERRING" }, { status: "COMPLETED" }] }
  }), /^Uploading your documents/);
  assert.match(resolveCustomerUploadStatusMessage({
    uploadProgress: { files: [{ status: "PROCESSING" }] }
  }), /^Uploading your documents/);
});

test("completed and partially failed uploads explain the next action", () => {
  assert.match(resolveCustomerUploadStatusMessage({
    uploadProgress: { files: [{ status: "COMPLETED" }] }
  }), /^Files uploaded successfully/);
  assert.match(resolveCustomerUploadStatusMessage({
    uploadProgress: { files: [{ status: "FAILED" }] }
  }), /^Some files need attention/);
});

test("authoritative order status takes precedence over the upload empty state", () => {
  assert.equal(resolveCustomerUploadStatusMessage({ jobStatus: "AWAITING_PAYMENT" }),
    "Your files are accepted. Complete payment to continue.");
  assert.equal(resolveCustomerUploadStatusMessage({ jobStatus: "QUOTE_EXPIRED" }),
    "Your quote has expired · Request a new quote.");
});

test("the latest known payment state remains visible without a job status payload", () => {
  assert.equal(resolveCustomerUploadStatusMessage({ paymentStatus: "VERIFIED" }),
    "Payment received. The shop is preparing your print.");
});

test("a fresh quote remains the primary next step after the upload is complete", () => {
  assert.match(resolveCustomerUploadStatusMessage({ quoteAvailable: true, uploadedFileCount: 1,
    uploadProgress: { files: [{ status: "COMPLETED" }] } }), /^Check the price/);
});


test("new uploads and selected files take priority over an older order status", () => {
  assert.match(resolveCustomerUploadStatusMessage({
    jobStatus: "AWAITING_SHOP_APPROVAL",
    uploadProgress: { files: [{ status: "TRANSFERRING" }] }
  }), /^Uploading your documents/);
  assert.equal(resolveCustomerUploadStatusMessage({
    jobStatus: "AWAITING_SHOP_APPROVAL",
    filesSelected: 1
  }), "Review your selected files and upload.");
});

test("partial failures remain visible while other files are still uploading", () => {
  assert.match(resolveCustomerUploadStatusMessage({
    jobStatus: "AWAITING_SHOP_APPROVAL",
    uploadProgress: { files: [{ status: "FAILED" }, { status: "TRANSFERRING" }] }
  }), /^Some files need attention while the remaining files are still uploading/);
});

test("the latest payment result overrides a stale payment-stage job status", () => {
  assert.equal(resolveCustomerUploadStatusMessage({
    jobStatus: "AWAITING_CUSTOMER_PAYMENT",
    paymentStatus: "VERIFIED"
  }), "Payment received. The shop is preparing your print.");
  assert.equal(resolveCustomerUploadStatusMessage({
    jobStatus: "AWAITING_CUSTOMER_PAYMENT",
    paymentStatus: "REJECTED"
  }), "The payment could not be confirmed. Please check with the shop before paying again.");
  assert.equal(resolveCustomerUploadStatusMessage({ paymentStatus: "REJECTED" }),
    "The payment could not be confirmed. Please check with the shop before paying again.");
});

test("later fulfilment status remains more useful than an already verified payment", () => {
  assert.equal(resolveCustomerUploadStatusMessage({
    jobStatus: "PRINTING",
    paymentStatus: "VERIFIED"
  }), "Your print is in progress.");
});


test("all customer order lifecycle states have specific, actionable messages", () => {
  const cases: Array<[string, string]> = [
    ["VALIDATING", "checking your files"],
    ["ANALYSING", "preparing the print options"],
    ["AWAITING_CUSTOMER_APPROVAL", "Review your print settings and quote"],
    ["QUEUED", "waiting in the shop's printer queue"],
    ["SUBMITTING_TO_SPOOLER", "being sent to the printer"],
    ["PDF_EXPORTING", "PDF is being prepared"],
    ["PARTIALLY_COMPLETED", "Some files are complete"],
    ["MIXED_OUTCOME", "Some files need attention"],
    ["MIXED_PROGRESS", "still being processed"]
  ];
  for (const [jobStatus, expected] of cases) {
    const message = resolveCustomerUploadStatusMessage({ jobStatus, uploadedFileCount: 1 });
    assert.ok(message.includes(expected), jobStatus + " should not fall through to a generic uploaded-files message.");
  }
});

test("partial refunds are described as partial rather than full refunds", () => {
  assert.match(resolveCustomerUploadStatusMessage({ jobStatus: "PARTIALLY_REFUNDED" }), /partial refund/i);
  assert.match(resolveCustomerUploadStatusMessage({ paymentStatus: "PARTIALLY_REFUNDED" }), /partial refund/i);
});


test("the shop-claimed and locally-ready states have distinct customer meanings", () => {
  const claimed = resolveCustomerUploadStatusMessage({ jobStatus: "LOCAL_QUEUE_CLAIMED" });
  const ready = resolveCustomerUploadStatusMessage({ jobStatus: "LOCAL_QUEUE_READY" });
  assert.match(claimed, /shop computer has picked up your request/i);
  assert.match(claimed, /preparing your file/i);
  assert.match(ready, /safely stored on the shop computer/i);
  assert.match(ready, /ready for the shop to print/i);
  assert.notEqual(claimed, ready);
});
