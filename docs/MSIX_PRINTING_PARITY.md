# SmartPrint MSIX — printing parity and release gates

## Non-negotiable requirement

The MSIX distribution is a second Windows package for the existing SmartPrint desktop application. It must not become a UI-only or reduced-functionality edition.

The MSIX package includes the same native Tauri desktop executable and the same self-contained .NET SmartPrint Agent build used by the existing Windows release. The Agent remains a Windows service running as NT AUTHORITY\LocalService; it is not replaced with a per-user background process.

The existing website installer (SmartPrint-Windows-Setup.exe) and its Agent service install/uninstall scripts remain unchanged. The shared Tauri startup has an MSIX-package-identity-gated WebView2 prerequisite check; unpackaged website EXE launches skip it. Android APK and AAB release paths are also independent.

## Required parity checks

| Area | Required behavior |
|---|---|
| Agent lifecycle | Windows registers `QRtoPrintSmartPrintAgent` as an automatic packaged service. The service runs under LocalService, matching the Agent binary's configured service name; it starts after install and remains running after upgrade. The EXE installer sets the friendly display name `QRtoPrint Smart Print Agent`; the `desktop6:Service` MSIX schema has no separate display-name attribute, so the Services.msc label may differ. SCM crash-recovery parity is a separate unresolved release blocker. |
| Desktop IPC | Existing named-pipe status, enrollment and network-selection operations remain available to the desktop UI. |
| Local API / phone-to-printer | Agent continues listening on TCP 17842; existing status, local upload, preview, photo-revision and phone-check endpoints remain in the same Agent binary. The packaged CORS allow-list contains the exact customer portal origin supplied by `SMARTPRINT_PUBLIC_BASE_URL` plus `http://tauri.localhost`; it does not use a wildcard or the development `localhost:3000` origin. The package builder verifies these settings after unpacking the signed MSIX. |
| LAN | MSIX firewall manifest supports profile/port scoping but not the EXE installer's `RemoteAddress=LocalSubnet` restriction. The package currently declares an all-profile inbound TCP 17842 rule. Agent middleware independently rejects remote clients outside the selected shop interface/subnet and sensitive endpoints retain pairing authorization. This is a security difference; test Private, Public and Domain profiles plus negative-network access. |
| Printer discovery | Same WindowsPrinterDiscovery and Windows spooler integration are used. |
| Printing | Same queue, worker, PDF rendering and print executor are used; no alternate print implementation is introduced. |
| Pairing and identity | Existing encrypted machine identity, enrollment, revocation, DPAPI and device identity behavior remain in the Agent. |
| Private printing | Existing encrypted local queue and protected temporary-document handling remain in the Agent. |
| Persistence | %ProgramData%\QRtoPrint is outside the package and must survive package upgrade and uninstall. |
| WebView2 | The MSIX bundles Microsoft's signed Evergreen standalone runtime installer. Before Tauri initializes, the packaged app checks the WebView2 registry registration and silently installs the bundled Evergreen runtime only if it is missing. The runtime remains Evergreen and receives Microsoft updates; no fixed runtime is frozen into the app. A clean-machine first-launch test remains a release gate. |

## Package implementation

Tauri v2's supported Windows bundle targets are NSIS and MSI, not MSIX. The MSIX is therefore assembled separately from the same SmartPrint.Desktop.exe and the same Agent publish output. The Tauri bundle configuration and existing Inno Setup website installer are not modified.

The package manifest declares:
- `windows.service` for `QRtoPrintSmartPrintAgent`, running as `localService` (matching `WindowsServiceLifetimeOptions.ServiceName` in the Agent binary);
- `packagedServices` as a restricted capability required by the service extension;
- `runFullTrust` as a restricted capability required by the full-trust Win32/Tauri desktop application;
- `privateNetworkClientServer` for the selected shop LAN;
- an all-profile inbound TCP 17842 firewall exception for Agent\SmartPrint.Agent.exe; Agent middleware continues to restrict remote clients to the selected interface and subnet;
- no `localSystemServices`, no `internetClient`, and no `internetClientServer`.

The desktop app is a full-trust Win32 process, not an AppContainer app. Microsoft documents that `internetClient` is unnecessary for medium-integrity full-trust desktop apps because they already have the user's network access. The Agent's cloud HTTPS requests therefore do not justify declaring that extra capability.

The package minimum OS is Windows 10 version 2004 (build 19041), matching packaged-service support. This minimum version does not, by itself, guarantee that WebView2 is present on every Windows edition or managed device.

The Agent appsettings.json is generated before packaging because MSIX contents are immutable. The API base URL and customer portal CORS origin are both embedded from `SMARTPRINT_PUBLIC_BASE_URL`, matching the existing website installer configuration; the Tauri origin is added separately. No post-install edit to the package directory is attempted.

## Website EXE and Store MSIX coexistence/migration

The website EXE and Store MSIX are alternative distributions, not side-by-side installations. They share TCP port 17842, the named pipe and machine-wide `%ProgramData%\QRtoPrint` data. Both installers and the Agent binary now use the same SCM service key `QRtoPrintSmartPrintAgent`. The EXE installer sets the friendly display name `QRtoPrint Smart Print Agent`; the `desktop6:Service` MSIX schema has no separate display-name attribute, so we do not claim the Services.msc label is identical. This removes the previous service-name mismatch. Both packages must not be installed side by side because they would compete for the same local API, pipe and machine data.

Switching distribution must be performed in this order; verify removal of the old distribution's own service key before installing the new one:
1. Uninstall the currently installed Windows distribution first.
2. Confirm its uninstaller removed the Agent service but retained `%ProgramData%\QRtoPrint`.
3. Install the other distribution.
4. Verify the same machine identity, enrollment, paired devices and queued jobs remain available.

The existing website EXE uninstaller intentionally preserves machine data. The MSIX uninstall validation also checks that the package service is removed while machine data remains. However, a full EXE-to-MSIX and MSIX-to-EXE identity/queue migration test on a machine that has actually used the EXE is still required before promising seamless migration. Do not tell customers that both packages can be installed together.

## Automated validation

.github/workflows/msix-validation.yml builds the desktop app and Agent, packages and signs an ephemeral CI MSIX, installs it, and verifies:
1. Windows registers and starts the Agent service as LocalService.
2. The existing named-pipe protocol responds to a status request.
3. The existing local HTTP API responds on TCP 17842.
4. Windows printer subsystem enumeration is available to the same Agent build.
5. The package firewall rule applies to the Agent executable and TCP 17842 across the intended Windows network profiles.
6. A package upgrade preserves %ProgramData%\QRtoPrint and keeps the Agent service running.
7. Package uninstall removes the packaged service but preserves machine data.
8. The Windows runner has a valid Evergreen WebView2 Runtime registration, and the package contains the Microsoft-signed standalone installer for machines where the runtime is absent.

The current `desktop6:Service` manifest schema exposes service name, startup type, start account, arguments and dependencies, but no SCM failure-action settings. The existing EXE installer configures SCM recovery with `sc.exe`; the current MSIX package does not yet demonstrate equivalent crash recovery. This is a release blocker. Do not claim crash-recovery parity until a Store-compatible method is implemented and a forced Agent-process crash test proves automatic restart.

The WebView2 runtime is preinstalled on Windows 11 and many eligible Windows 10 devices, but some devices may not have it. The MSIX bundles Microsoft's signed Evergreen x64 standalone installer. Before Tauri initializes, the packaged app checks the WebView2 runtime registration and silently installs the bundled runtime only when missing. CI verifies that the download exceeds 50 MiB and has a valid Microsoft Authenticode signature. The runtime remains Evergreen and receives Microsoft updates; no fixed runtime is frozen into the app. A clean-machine first-launch test with WebView2 absent remains a release gate. Do not assume that the presence of Microsoft Edge browser alone proves the WebView2 Runtime is available.

### Windows App Certification Kit (WACK)

WACK must be run on the final production-identity MSIX in an elevated, active Windows desktop session. Microsoft documents that WACK cannot run from Session 0, so it is deliberately not represented as a GitHub-hosted CI pass.

After downloading the manual workflow artifact, import the accompanying public certificate for local sideload testing and run the following from an elevated interactive PowerShell window:

```powershell
.\scripts\validate-msix-wack.ps1 `
  -PackagePath "C:\path\to\SmartPrint-Windows-Store.msix" `
  -CertificatePath "C:\path\to\SmartPrint-MSIX-DevCert.cer"
```

The script invokes `appcert.exe`, validates the complete XML report and package identity/version, and fails unless the overall result is `PASS`. Keep the XML/HTML reports with the release evidence. Install the Windows App Certification Kit component from the Windows SDK if `appcert.exe` is not present.

This CI validation does not prove physical printer output: a hosted runner does not provide the customer's actual printer, USB/network topology, spooler driver or phone. Before release, test the exact production-signed MSIX on clean Windows machines with representative USB and network printers, a phone on the selected shop LAN, actual PDF/photo jobs, cancellation, queue recovery, sleep/resume, upgrade and uninstall. Confirm the Windows firewall permits a phone on the selected shop LAN to reach TCP 17842 while the Agent still rejects clients outside the selected network.

## Microsoft Store approval is a separate gate

Microsoft classifies both `packagedServices` and `runFullTrust` as restricted capabilities. Partner Center must receive an explanation for each declared restricted capability. The `packagedServices` request is specifically the high-risk service approval; Microsoft warns it is not approved in most Store submissions. A successful local install or CI test does not grant Store approval.

Before publishing the MSIX:
1. Reserve the MSIX app identity in Partner Center and use its exact package Name and Publisher values in the manifest. Do not reuse the EXE/MSI submission API or its package type.
2. Request approval for `runFullTrust` and `packagedServices`, explaining each separately.
3. Explain that SmartPrint's existing printing architecture requires a LocalService Windows service to discover printers, run the durable print worker, protect local documents and expose the existing LAN endpoint to paired shop devices.
4. Explain that the Tauri desktop app needs full trust to use native Win32 desktop, Windows printing, named-pipe IPC and the local Agent API; it is not an AppContainer application.
5. Provide parity test evidence and explain why a per-user process would reduce unattended printing, multi-user reliability and access to machine-wide printer queues.
6. Do not submit the package for Store certification until Microsoft has approved the restricted capabilities and clean-machine/physical-printer acceptance tests pass.

The existing Store Submission API script in this repository is specifically for MSI/EXE packages. Do not submit an MSIX by changing `packageType` or reusing the EXE package URL. For the **first MSIX publication**, upload the package through the MSIX Packages page in Partner Center. Microsoft accepts `.msix` directly and recommends `.msixupload`; the current validation workflow produces a `.msix` candidate, not an `.msixupload`. Complete the first submission, age rating, listing, restricted-capability review and certification in Partner Center; the Microsoft Store Developer CLI cannot perform an app's first publication.

After the app has been published once, Microsoft supports the Microsoft Store Developer CLI (preview) for subsequent MSIX updates. It can upload a supplied `.msix` or `.msixupload` and create a draft with `msstore publish <project-path> --inputFile <package.msix> --noCommit`. CI configuration uses the Partner Center Entra tenant ID, Seller ID, client ID and a client secret or certificate. The CLI's current MSIX update support is for **free products only**. Keep `--noCommit` for the first automated update workflow so a human can review the draft in Partner Center before certification. Do not run `msstore publish` while another draft is pending: the CLI recreates the draft from the last published submission and can discard staged metadata. This repository does not yet automate that post-first-publication CLI path. The same validation workflow has a manual production-identity candidate build path: supply the exact Partner Center package Name, Publisher, app display name, publisher display name and four-part version. It creates a temporary self-signed certificate whose subject matches the Partner Center Publisher, signs and locally tests the package, then uploads a clearly labelled **validation candidate** MSIX plus a public .cer for optional local sideload/WACK testing. The candidate is uploaded before the final service-recovery gate so that it can be inspected and used to seek Microsoft guidance, even when the gate correctly fails. A failed workflow or candidate artifact is **not approval to submit**; do not upload the MSIX to Partner Center as a release package until every release gate, WACK and physical-print acceptance test passes. Upload only the .msix to Partner Center; do not upload the .cer. Microsoft re-signs the package after certification. This path does not require the website EXE's code-signing PFX and does not submit the package automatically.

## Release status

A package build or successful local installation is not release approval. Release readiness requires automated checks, physical printing parity, WebView2 availability, a canonical public privacy-policy URL for this Win32/Desktop Bridge app, Microsoft restricted-capability approval, package identity/publisher validation and Partner Center certification. If any of those fail, keep the existing website EXE available and do not advertise the MSIX as ready.
