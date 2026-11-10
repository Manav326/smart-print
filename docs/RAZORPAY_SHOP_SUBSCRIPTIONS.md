# SmartPrint shop subscription payments (Razorpay)

Shop-owner subscription charges are a separate payment purpose from the payments that a shop collects from its own print customers.

## Payment flow

1. The authenticated shop owner opens **Trial & subscription** and selects a published paid plan version and billing interval.
2. SmartPrint first commits a durable, tenant-scoped payment intent, then creates the Razorpay order on the API using the amount and currency from that immutable published plan version. The portal cannot supply or override the price. The online order ceiling is ₹1,00,000.00.
3. Razorpay Checkout collects the payment.
4. The portal submits the Razorpay order ID, payment ID and checkout signature to SmartPrint.
5. The API verifies the checkout signature with the Razorpay API key secret, retrieves the payment directly from Razorpay and checks the payment ID, order ID, amount, currency and captured status.
6. After verification, the API creates the active tenant subscription and platform audit event in the same database transaction, and records the captured Razorpay payment.
7. A Razorpay webhook provides the recovery path when the customer closes the browser, the checkout callback is lost, or browser-to-API verification cannot finish. The webhook is signature-verified from the untouched request body and independently checks payment details with Razorpay before activating a plan.

The shop subscription order/payment records and webhook event inbox are separate from `PrintPayments`, `PrintBatchPayments`, shop UPI/cash settings, customer payment verification and print refunds. Existing customer print-payment routes are not repurposed.

If order creation returns an ambiguous network result, SmartPrint looks up the order by its unique receipt and stored payment-intent ID before presenting checkout. An unresolved result stays in a reconciliation state; SmartPrint does not blindly create a second Razorpay order. Shop owners can resume the same pending payment from the subscription page. Before reopening an existing order, SmartPrint fetches its payments from Razorpay and reconciles any capture, even when the webhook has not arrived. If the provider status cannot be confirmed, checkout is not reopened. Because Razorpay does not expose a general cancel operation for an unpaid Order, the portal does not pretend to cancel it or silently create another payable order.

## Server configuration

Set these API environment variables in the SmartPrint deployment environment (not in portal/browser configuration):

| Variable | Purpose |
| --- | --- |
| `RAZORPAY_KEY_ID` | Razorpay API key ID; this public checkout key is returned only for the active checkout request |
| `RAZORPAY_KEY_SECRET` | Server-only Razorpay API and checkout-signature secret |
| `RAZORPAY_WEBHOOK_SECRET` | Server-only webhook signing secret configured in the Razorpay Dashboard |
| `RAZORPAY_BASE_URL` | Optional; defaults to `https://api.razorpay.com/v1` |

The same API key ID/secret pair must be used for order creation and payment lookup. Keep test keys for testing and live keys for production. Never place either secret in portal build variables or commit actual credentials.

## Razorpay Dashboard webhook

Create a webhook pointing to:

```text
https://<smartprint-public-origin>/api/v1/webhooks/razorpay/subscriptions
```

Subscribe to these events:

- `payment.captured`
- `payment.failed`
- `order.paid`

Set the webhook secret in `RAZORPAY_WEBHOOK_SECRET` on the SmartPrint API deployment. The webhook secret is distinct from the Razorpay API key secret. After changing the Dashboard secret, update the API environment to exactly match it and restart/redeploy the API.

The API validates `X-Razorpay-Signature` using HMAC-SHA256 over the exact raw request body. Do not add body parsing or JSON reserialization before signature validation. It deduplicates with `x-razorpay-event-id`, serializes subscription activation per tenant, and tolerates duplicate/out-of-order deliveries. A failed event cannot downgrade an already captured payment, while a later captured event can recover an earlier failed attempt.

## Subscription periods and renewal

The subscription period begins when Razorpay confirms the payment is captured and SmartPrint activates the purchased plan. A monthly plan receives one month; an annual plan receives one year. A same-plan renewal extends from the current active period end so unused time is not lost. A different plan takes effect immediately. Paid subscriptions are one-time Razorpay Orders/Checkout payments: automatic recurring billing or automatic renewal is not enabled by this feature. The shop owner returns to the subscription page to renew or change the published plan.

A captured payment whose referenced plan version has become unavailable is recorded as `CAPTURED_REVIEW_REQUIRED`; it is not silently discarded or treated as an active entitlement. It requires platform support review. An authorised-but-not-captured payment does not activate the plan.

## Local and CI testing

Without Razorpay credentials, the portal cannot start a real checkout. Unit tests verify signature, payload parsing, mismatch rejection and front-end price/status logic. PostgreSQL integration tests use a fake Razorpay HTTP endpoint and the CI PostgreSQL service to test order creation, captured checkout, webhook recovery, duplicate delivery, out-of-order delivery and failure/mismatch cases. Use Razorpay test credentials and a separate test webhook secret for an end-to-end Dashboard callback test before live use.
