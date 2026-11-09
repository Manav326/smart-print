# SmartPrint shop subscription payments — Razorpay setup

Shop-owner subscription billing uses Razorpay Standard Checkout. It is separate from customer print-job payments (UPI/cash), print payment history and refunds; those flows are unchanged.

## Server configuration

Set the following values in the API deployment environment. Do not put secrets in portal build variables or commit them to the repository.

- `RAZORPAY_KEY_ID`: Razorpay API key ID
- `RAZORPAY_KEY_SECRET`: Razorpay API key secret
- `RAZORPAY_WEBHOOK_SECRET`: the webhook secret configured for this endpoint
- `RAZORPAY_BASE_URL`: optional; defaults to `https://api.razorpay.com/v1`

Checkout remains unavailable unless both API credentials and the webhook secret are configured. Restart/redeploy the API after changing secrets.

## Razorpay webhook

Create a webhook pointing to:

`https://<your-public-api-host>/api/v1/webhooks/razorpay/subscriptions`

Subscribe to these payment events:

- `payment.authorized`
- `payment.captured`
- `payment.failed`

Use the same webhook signing secret in `RAZORPAY_WEBHOOK_SECRET`. The API verifies the signature against the exact raw request body and deduplicates events using `X-Razorpay-Event-Id`. Failed reconciliation returns a retryable response so Razorpay can redeliver it.

## Payment and activation behaviour

The API calculates the amount from the selected published plan version, creates the Razorpay order, and returns only the public key ID to the signed-in shop owner. The portal then opens Razorpay Checkout and posts the order/payment/signature response to the API.

The API verifies the checkout HMAC, retrieves the payment from Razorpay, and checks order ID, amount, currency and captured state. A plan is activated only after capture is verified. The webhook independently reconciles captured payments and converges idempotently with checkout verification.

Only INR plans are currently offered for checkout. Monthly and annual billing are one-time payments; there is no automatic debit or recurring mandate. The existing platform-admin subscription assignment remains available.

## Testing

Use Razorpay test-mode credentials and the configured webhook endpoint before enabling live keys. The API test suite includes pricing/signature unit coverage and PostgreSQL-backed order, verification, webhook, idempotency and tenant-isolation tests.
