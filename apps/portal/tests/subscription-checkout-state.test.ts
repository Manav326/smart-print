import assert from "node:assert/strict";
import test from "node:test";
import {
  formatSubscriptionAmount,
  getPaidBillingIntervals,
  isUnsettledSubscriptionPayment,
  subscriptionPaymentActionLabel,
  subscriptionPaymentStatusLabel,
  subscriptionPaymentStatusMessage
} from "../src/subscription-checkout-state.ts";

test("only published billing intervals with a positive price are offered for checkout", () => {
  assert.deepEqual(
    getPaidBillingIntervals({ monthlyPriceMinor: 49900, annualPriceMinor: 499900 }),
    ["Monthly", "Annual"]
  );
});

test("a free interval is not sent to Razorpay while a paid interval remains selectable", () => {
  assert.deepEqual(
    getPaidBillingIntervals({ monthlyPriceMinor: 0, annualPriceMinor: 499900 }),
    ["Annual"]
  );
});

test("null and negative prices do not create payable choices", () => {
  assert.deepEqual(
    getPaidBillingIntervals({ monthlyPriceMinor: null, annualPriceMinor: -1 }),
    []
  );
});

test("both unset prices produce no checkout choices", () => {
  assert.deepEqual(
    getPaidBillingIntervals({ monthlyPriceMinor: null, annualPriceMinor: null }),
    []
  );
});

test("subscription plan price is formatted from paise in Indian currency format", () => {
  assert.equal(formatSubscriptionAmount(49900, "INR"), "₹499.00");
  assert.equal(formatSubscriptionAmount(125050, "INR"), "₹1,250.50");
});

test("only unresolved payment states block a new subscription order", () => {
  assert.equal(isUnsettledSubscriptionPayment("CREATING"), true);
  assert.equal(isUnsettledSubscriptionPayment("CREATION_UNKNOWN"), true);
  assert.equal(isUnsettledSubscriptionPayment("CREATED"), true);
  assert.equal(isUnsettledSubscriptionPayment("FAILED"), true);
  assert.equal(isUnsettledSubscriptionPayment("CAPTURED_REVIEW_REQUIRED"), true);
  assert.equal(isUnsettledSubscriptionPayment("CAPTURED"), false);
});

test("pending subscription payments expose an action appropriate to their recovery state", () => {
  assert.equal(subscriptionPaymentActionLabel("CREATED"), "Continue payment");
  assert.equal(subscriptionPaymentActionLabel("FAILED"), "Retry payment");
  assert.equal(subscriptionPaymentActionLabel("CREATION_UNKNOWN"), "Check order status");
  assert.equal(subscriptionPaymentStatusLabel("CREATION_UNKNOWN"), "Confirmation pending");
});

test("captured payment is reported as verified and active", () => {
  assert.equal(
    subscriptionPaymentStatusMessage("CAPTURED"),
    "Payment verified. Your subscription is active."
  );
});

test("captured payment requiring plan review is not described as active", () => {
  assert.match(
    subscriptionPaymentStatusMessage("CAPTURED_REVIEW_REQUIRED"),
    /must review.*before access is activated/i
  );
});

test("failed and still-pending payments have distinct recovery messages", () => {
  assert.match(subscriptionPaymentStatusMessage("FAILED"), /try again/i);
  assert.match(subscriptionPaymentStatusMessage("CREATED"), /Complete checkout/i);
});
