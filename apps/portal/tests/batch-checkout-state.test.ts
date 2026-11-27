import assert from "node:assert/strict";
import test from "node:test";
import { shouldBlockBatchCheckoutForExpiredQuote, shouldBlockQuoteRefreshForStatus } from "../src/batch-checkout-state.ts";

test("expired accepted quotes block checkout before payment starts", () => {
  assert.equal(shouldBlockBatchCheckoutForExpiredQuote(null, true), true);
  assert.equal(shouldBlockBatchCheckoutForExpiredQuote(undefined, true), true);
});
test("quote expiry does not hide an active or verified batch payment", () => {
  for (const status of ["AWAITING_CUSTOMER_PAYMENT","CUSTOMER_REPORTED","CASH_AWAITING_PAYMENT","VERIFIED"]) {
    assert.equal(shouldBlockBatchCheckoutForExpiredQuote(status, true), false, status);
  }
});
test("a rejected attempt still requires a fresh quote after expiry", () => {
  assert.equal(shouldBlockBatchCheckoutForExpiredQuote("REJECTED", true), true);
});
test("an unexpired quote never blocks checkout", () => {
  for (const status of [null,"REJECTED","AWAITING_CUSTOMER_PAYMENT","VERIFIED"]) {
    assert.equal(shouldBlockBatchCheckoutForExpiredQuote(status, false), false);
  }
});

test("quote refresh can renew an expired quote before payment starts", () => {
  for (const status of ["AWAITING_SHOP_APPROVAL", "AWAITING_PAYMENT"]) {
    assert.equal(shouldBlockQuoteRefreshForStatus(status), false, status);
  }
});

test("quote refresh cannot replace a payment-started, paid, refunded, or fulfilled order", () => {
  for (const status of [
    "AWAITING_CUSTOMER_PAYMENT",
    "CUSTOMER_REPORTED", "CASH_AWAITING_PAYMENT", "PAYMENT_REPORTED", "VERIFIED",
    "PARTIALLY_REFUNDED", "PAID_AUTHORISED", "LOCAL_QUEUE_CLAIMED", "LOCAL_QUEUE_READY",
    "PRINT_AUTHORISED", "PRINT_SUBMITTING", "SPOOLER_ACCEPTED", "PRINTING",
    "OUTPUT_UNCONFIRMED", "COMPLETED", "PDF_EXPORTED", "FAILED", "REJECTED",
    "REFUNDED", "CANCELLED"
  ]) {
    assert.equal(shouldBlockQuoteRefreshForStatus(status), true, status);
  }
});

test("quote refresh remains available before acceptance or after a rejected payment when a quote has expired", () => {
  for (const status of [null, "AWAITING_CONFIGURATION", "UPLOADED", "QUOTE_EXPIRED", "PAYMENT_REJECTED"]) {
    assert.equal(shouldBlockQuoteRefreshForStatus(status), false, String(status));
  }
});
