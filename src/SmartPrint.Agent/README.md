# Windows Print Agent

This is a Windows Service. It pairs with one shop using a single-use enrollment code, stores the returned credential with Windows DPAPI (LocalMachine), maintains an independent cloud heartbeat, synchronizes Windows-installed printer queue inventory on its own schedule, and processes the protected local print queue in a separate worker loop. Heartbeat and inventory polling default to every 30 seconds and use `Agent:HealthSyncIntervalSeconds` (10–60 seconds). Protected print-work polling uses `Agent:PollIntervalSeconds` (10–300 seconds).

## Local and production portal URL

The portal, QR links and API use one canonical HTTPS origin: PUBLIC_BASE_URL in .env or .env.local. Portal API requests use the same origin under /api, reverse-proxied to the API by Nginx.

Run .\scripts\docker-up-local.ps1 for local end-to-end testing. Create a named Cloudflare Tunnel and publish the chosen hostname to http://portal:80. The helper stores the tunnel token in .secrets/cloudflare/tunnel-token, which is ignored by Git. Do not put the token in source control or share it with customers.

Pair the Agent with the same PUBLIC_BASE_URL. After an authenticated heartbeat, the Agent synchronises the canonical URL and its Local API CORS origin from the API without changing its DPAPI-protected identity. Keep any previous Cloudflare hostname route active during a migration so existing Agents can receive the new address and previously printed QR codes remain usable.

## Pair and install

### Shop-owner setup (recommended)

1. In the shop workspace, open **Print Agent** and generate a one-time pairing code. It expires after 10 minutes.
2. On the Windows 10/11 computer connected to the shop printers, download and run the official **SmartPrint Windows Setup** installer from the QRtoPrint Downloads page or the latest GitHub release.
3. Approve the Windows administrator prompt. In the setup wizard, enter the pairing code from the shop workspace. The wizard pairs the computer, installs the Windows Service, configures its local endpoint and starts the service.
4. Return to **Print Agent** in the shop workspace. Wait for the computer heartbeat and discovered printer queues; do not treat installation alone as proof that a printer is ready.

The installer enables the local status and direct-upload endpoint. The Agent allows TCP 17842 only from the connected local subnet, regardless of whether Windows labels that shop connection Public or Private. It does not open the endpoint to the internet. The authenticated heartbeat keeps the runtime browser CORS origin aligned with PUBLIC_BASE_URL.

Phone hotspots are supported when they provide a private IPv4 local network. After connecting the Windows computer to the hotspot, open the desktop **Shop Connection** panel and explicitly select the new hotspot from the connection list. SmartPrint keeps the previous shop-network choice saved until the owner confirms the new connection; it never silently switches the selected network when the SSID changes. No Windows network-profile change is required: the installer firewall rule applies to all Windows profiles and remains limited to the local subnet. Run the short phone check again after selecting the hotspot. Use a second phone connected to the hotspot as the customer device; the phone providing the hotspot is not itself a Wi-Fi client on that hotspot. Some hotspots isolate connected devices from one another; when that happens, SmartPrint's phone check will fail safely and a shop Wi-Fi router or a hotspot that permits client-to-client traffic is required.

### Advanced / developer setup

For a release ZIP or manual recovery, an administrator can pair and install from elevated PowerShell with the scripts in the package:

   .\scripts\pair-agent.ps1 -ApiBaseUrl "https://YOUR_PUBLIC_BASE_URL" -AgentExePath ".\SmartPrint.Agent.exe"

   .\scripts\install-agent-service.ps1 -AgentExePath ".\SmartPrint.Agent.exe" -CustomerPortalOrigin "https://YOUR_PUBLIC_BASE_URL"

To remove the Windows Service, run scripts\uninstall-agent-service.ps1 from an elevated PowerShell. This leaves the encrypted machine identity intact; revoke the agent in the shop workspace before disposing of or replacing the computer.

## Current capabilities

Direct customer document upload over shop Wi-Fi is implemented. The customer portal obtains a one-use upload grant from the API, sends document bytes to this paired Agent, and the Agent validates the hash and document structure before storing the file in its DPAPI-protected local queue. The API retains only job/document metadata, SHA-256, page analysis and quote/payment/dispatch state. The same PrintJob is later authorised by the shop and claimed by this Agent after manual payment verification; the cloud is not asked to download a LOCAL_AGENT document.

Cloud-assisted uploads use the shop's HTTPS customer portal and API, and the customer UI selects this path by default when the shop's subscription includes Cloud-assisted Printing. Documents are sent to the cloud service over HTTPS, then handed to the paired Agent over its authenticated HTTPS connection. This is the encrypted-in-transit option.

Shop Wi-Fi direct upload is a separate, opt-in local-network path. The customer portal itself must still be HTTPS, and browsers may require the customer to grant Local Network Access permission. The browser connects to the Agent's private IPv4 endpoint on TCP 17842 using local HTTP. This is intentional: customer phones on arbitrary shop networks do not automatically trust a self-signed certificate issued by an individual Windows PC, and a publicly trusted certificate for a changing private IP would require extra per-shop DNS/router configuration or a public tunnel. The direct local transfer is therefore **not encrypted in transit**. It remains restricted to the selected shop subnet, trusted portal origins, one-use upload grants and the paired Agent identity. The UI labels this mode clearly; customers who require encrypted document transfer should choose Cloud-assisted upload when it is included in the shop's plan.

All Agent-to-cloud API traffic is HTTPS-only for remote hosts. HTTP is permitted only for loopback development. Existing installations with a legacy remote HTTP API address migrate to the HTTPS origin configured by the Windows installer before an Agent secret is sent; without a valid HTTPS configuration, cloud requests fail closed. The current canonical HTTPS portal origin and CORS allow-list are refreshed from the Agent's authenticated heartbeat. The Agent does not open port 17842 to the internet and does not ask the shop owner to change the Windows network profile.

- Queries Win32_Printer through WMI for installed queue name, driver, port, status and default queue.
- Supports multiple installed Windows queues on one paired desktop. SmartPrint keeps distinct queue names separate even when they share a port; the shop workspace flags possible duplicates for owner review rather than merging them automatically.
- A print batch is assigned to exactly one printer queue. The shop owner chooses one queue for the whole batch, the Agent verifies the same assignment for every accepted file, and the shop queue shows the actual Windows printer/queue for each file after output.
- Sends printer inventory to the paired shop tenant using agent-specific authentication headers.
- Exposes a read-only local status endpoint at `http://<shop-computer-LAN-IP>:17842/api/v1/local/status`. It returns only the paired agent ID, protocol version, readiness state and configured API origin; it does not expose the agent secret, queue or documents. The Windows desktop uses the separate local-only, ACL-protected named pipe for status fallback and enrollment.
- Reports only the selected shop connection's RFC1918 IPv4 endpoint in the authenticated cloud heartbeat. On Windows, the desktop Shop Connection panel displays the connected Wi-Fi name (SSID) when available, lets the owner choose an available adapter, and confirms phone reachability with a five-minute, single-use QR test. The result expires after ten minutes and is not treated as a permanent online signal.
- The local listener is IPv4-only and checks both the remote phone address and the local destination address against the selected adapter's IPv4 subnet. Requests arriving through another local adapter are rejected even though the listener is bound to all IPv4 interfaces. If the saved Wi-Fi/adapter disappears, the Agent stops advertising a replacement network until the owner selects one.
- The installer adds an inbound rule for TCP 17842 restricted to the local subnet and the Agent executable. The desktop Shop Connection panel lets the owner select the adapter and verify phone-to-Agent access with a short-lived one-time QR test. The Agent synchronises its current public origin through its authenticated heartbeat.
- Refreshes the Agent heartbeat and printer inventory every 30 seconds (configurable through `Agent:HealthSyncIntervalSeconds` from 10 to 60 seconds, keeping both fresh within the server's two-minute availability window). Protected print-work polling is independently configurable through `Agent:PollIntervalSeconds` from 10 to 300 seconds.
- Marks previously discovered queues unavailable when they disappear from that computer's WMI inventory.
- Stores the agent secret and locally received print documents encrypted using DPAPI LocalMachine. The protected queue directory is restricted to SYSTEM and local Administrators.
- Claims only paid/authorised jobs assigned to one of this agent's live printer queues. The claim has a three-minute lease and a tenant/agent ownership check.
- Downloads documents only while the lease is active, verifies the source SHA-256 and size, writes an encrypted local document plus durable manifest, and then acknowledges local receipt to the cloud.
- Retries ACK_PENDING acknowledgements after restart and reuses an already stored job rather than downloading a second local copy.
- Executes only shop-authorised jobs after proving the matching protected local document. Physical printers rasterise PDF/JPEG/PNG and submit once through the Windows spooler. Microsoft Print to PDF is the primary PDF-export route: the Agent submits through the installed Microsoft driver with an explicit output path (no interactive Save As dialog), verifies the resulting PDF, and uses the SmartPrint PDF renderer only after a confirmed pre-submission rejection or a completed spooler job that produced no file. If Windows still has the job queued or the output is ambiguous, the Agent marks it unconfirmed rather than creating a possible duplicate.
- Persists SUBMITTING before the first spooler call, reconciles the stable document name after restart, and never automatically resubmits an uncertain attempt.
- Microsoft Print to PDF exports are written atomically to `C:\\Users\\Public\\Documents\\QRtoPrint Exports` by default. The shop operator can open the saved PDF directly; the API records its file name, path, size and SHA-256. The destination can be changed with `Agent:PdfExportDirectory`. Interrupted exports are reconciled from the final file before any retry.
- Reports spooler acceptance and queue status separately from physical completion. Only the shop operator can confirm that physical pages actually came out. PDF exports are complete only after the saved file is verified.
- Runs a six-hour local retention sweep. Expired, unfinalised uploads are removed; confirmed physical-print and reported PDF-export source documents and managed PDF exports are removed after `Agent:CompletedDocumentRetentionDays` (default 30, configurable from 7 to 3650 days). Output-unconfirmed jobs remain available for operator recovery.
