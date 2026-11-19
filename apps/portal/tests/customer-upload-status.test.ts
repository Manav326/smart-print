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
