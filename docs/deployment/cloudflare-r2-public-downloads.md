# SmartPrint public release downloads on Cloudflare R2

SmartPrint keeps GitHub Releases as the build provenance and audit record. When the optional R2 configuration is present, the release workflow also publishes the exact verified Windows installer and signed Android APK to a dedicated R2 bucket. Customers download those immutable objects directly from a Cloudflare custom domain and its cache.

The existing same-origin API/GitHub release route remains the fallback. Do not put customer print documents, shop previews, Private Printing files, or other user data in this bucket.

## Cloudflare setup (one time)

1. Create a dedicated R2 bucket, for example `smartprint-public-downloads`. Do not reuse a bucket containing unrelated or private objects: the release workflow applies the bucket's CORS policy for the public SmartPrint portal.
2. In the bucket's **Settings → Public access → Custom Domains**, connect a hostname such as `downloads.smartprint.example` from a zone in the same Cloudflare account. Wait until the custom domain is active. Do not use the `r2.dev` development URL; it does not provide Cloudflare cache, WAF or bot-management features.
3. In the bucket's **Settings → CORS Policy**, add this one-time policy. Replace the example origin with the exact `SMARTPRINT_PUBLIC_BASE_URL` origin. The policy is intentionally restricted to browser reads from the SmartPrint portal:
   
   ```json
   {
     "rules": [
       {
         "allowed": {
           "origins": ["https://smartprint.example"],
           "methods": ["GET", "HEAD"],
           "headers": ["Accept", "Range", "If-Range"]
         },
         "exposeHeaders": [
           "Accept-Ranges",
           "Content-Length",
           "Content-Range",
           "Content-Disposition",
           "ETag",
           "Last-Modified",
           "CF-Cache-Status"
         ],
         "maxAgeSeconds": 86400
       }
     ]
   }
   ```
4. Create an R2 S3 API token scoped to this bucket with **Object Read & Write** permission. Store its access key and secret as GitHub Actions secrets. Never store them in `.env`, source control, a browser bundle, or the SmartPrint API container. The GitHub token intentionally has object-only scope; it cannot change bucket configuration.

Cloudflare references:
- R2 custom domains and cache: https://developers.cloudflare.com/cache/interaction-cloudflare-products/r2/
- R2 CORS: https://developers.cloudflare.com/r2/buckets/cors/
- R2 S3 API endpoint and region: https://developers.cloudflare.com/r2/api/s3/api/

## GitHub Actions configuration

Set these repository **Actions variables**:

| Variable | Value |
| --- | --- |
| `SMARTPRINT_PUBLIC_BASE_URL` | Existing public portal origin, for example `https://smartprint.example` |
| `CLOUDFLARE_R2_ACCOUNT_ID` | Cloudflare account ID (32 hexadecimal characters) |
| `CLOUDFLARE_R2_BUCKET` | Dedicated bucket name, for example `smartprint-public-downloads` |
| `PUBLIC_DOWNLOAD_BASE_URL` | The R2 custom-domain origin, for example `https://downloads.smartprint.example` |

Set these repository **Actions secrets**:

| Secret | Value |
| --- | --- |
| `CLOUDFLARE_R2_ACCESS_KEY_ID` | R2 S3 access key ID |
| `CLOUDFLARE_R2_SECRET_ACCESS_KEY` | R2 S3 secret access key |

The bucket CORS policy is configured once in the Cloudflare dashboard; the release workflow does not modify bucket settings. The R2 variables and both secrets are an all-or-nothing configuration. If all are empty, release publication continues to GitHub Releases and the current API proxy remains the customer download origin. If only some are set, the release workflow fails rather than silently publishing a partially configured R2 release.

When configured, the workflow:
- Rechecks the local SHA-256 checksums and GitHub draft asset sizes/digests.
- Uploads objects at `<tag>/<filename>`, for example `v1.2.3/SmartPrint-Windows-Setup.exe`.
- Sets a one-year immutable cache policy, attachment filename and SHA-256 object metadata.
- Verifies the preconfigured bucket CORS policy through the public custom domain.
- Checks R2 object size, SHA-256 metadata and cache policy.
- Probes each public custom-domain object with a one-byte Range request and validates HTTP 206, CORS, Content-Range and immutable cache headers.
- Publishes the GitHub Release only after the configured R2 delivery path passes its checks.

Published version keys are immutable. A failed draft release can be retried; an already-published tag cannot be overwritten by the release workflow.

## Activate direct R2 delivery in SmartPrint

Set this optional value in the deployment `.env`:

```dotenv
PUBLIC_DOWNLOAD_BASE_URL=https://downloads.smartprint.example
```

Then redeploy the API using the normal SmartPrint deployment procedure. The API exposes this validated HTTPS origin in `/downloads/status`. The portal constructs the version-pinned R2 URL from it.

When R2 is enabled, the browser tries the R2 custom domain first. A CORS, network, HTTP or missing-object failure falls back to the existing same-origin `/downloads/<version>/<asset>` route, which continues to stream from GitHub. If the optional value is blank, customers use the existing route directly.

The direct R2 response is public and immutable, so the browser HTTP cache can accelerate repeat downloads on the same device. Private document endpoints remain no-store and are not affected.

## Operational checks

After a real release and deployment:

1. Open the versioned `.exe` or `.apk` URL in the browser Network panel. Confirm the hostname is the R2 custom domain, not the API host or `r2.dev`.
2. Confirm the response is HTTP 200 for a full download and HTTP 206 for a byte-range request.
3. Confirm `CF-Cache-Status` transitions from a cold MISS to HIT on a repeat request from the same Cloudflare location. A different location may have a cold cache; Smart Tiered Cache can reduce repeated origin fills.
4. Confirm the response includes `Cache-Control: public, max-age=31536000, immutable`, `Accept-Ranges`, `Content-Length`, and the correct CORS origin.
5. Compare cold and warm TTFB, sustained MB/s, completion time and failures on Android and Windows devices over both Wi-Fi and mobile networks.

Cloudflare's edge cacheable-file-size limit is plan-dependent even though R2 can store much larger objects. Check the current zone plan limit against both package sizes. A file larger than the edge cache limit can still be delivered from R2 but will not receive a normal edge-cache HIT.

Do not claim a speed improvement from the presence of R2 alone. Confirm warm-cache HITs and compare sustained receiving throughput on real customer networks before declaring the rollout complete.
