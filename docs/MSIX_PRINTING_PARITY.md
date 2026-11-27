# SmartPrint MSIX — printing parity and release gates

## Non-negotiable requirement

The MSIX distribution is a second Windows package for the existing SmartPrint desktop application. It must not become a UI-only or reduced-functionality edition.

The MSIX package includes the same native Tauri desktop executable and the same self-contained .NET SmartPrint Agent build used by the existing Windows release. The Agent remains a Windows service running as NT AUTHORITY\LocalService; it is not replaced with a per-user background process.

The existing website installer (SmartPrint-Windows-Setup.exe) and its service installer/uninstaller remain independent and unchanged by the MSIX packaging path. Android APK and AAB release paths are also independent.

## Required parity checks

| Area | Required behavior |
|---|---|
| Agent lifecycle | Windows registers QRtoPrintSmartPrintAgent as an automatic packaged service under LocalService; it starts after install and is running after upgrade. SCM crash-recovery parity is a separate unresolved release blocker. |
| Desktop IPC | Existing named-pipe status, enrollment and network-selection operations remain available to the desktop UI. |
| Local API | Agent continues listening on TCP 17842; existing status, local upload, preview, photo-revision and phone-check endpoints remain in the same Agent binary. |
| LAN | Package declares private-network access and an inbound all-profile firewall exception for the Agent on TCP 17842. Agent-side selected-interface/subnet validation remains authoritative and must be tested on Private, Public and Domain profiles. |
| Printer discovery | Same WindowsPrinterDiscovery and Windows spooler integration are used. |
| Printing | Same queue, worker, PDF rendering and print executor are used; no alternate print implementation is introduced. |
| Pairing and identity | Existing encrypted machine identity, enrollment, revocation, DPAPI and device identity behavior remain in the Agent. |
| Private printing | Existing encrypted local queue and protected temporary-document handling remain in the Agent. |
| Persistence | %ProgramData%\QRtoPrint is outside the package and must survive package upgrade and uninstall. |
| WebView2 | The MSIX bundles Microsoft's signed Evergreen standalone runtime installer. Before Tauri initializes, the packaged app checks the WebView2 registry registration and silently installs the bundled Evergreen runtime only if it is missing. The runtime remains Evergreen and receives Microsoft updates; no fixed runtime is frozen into the app. A clean-machine first-launch test remains a release gate. |

## Package implementation

Tauri v2's supported Windows bundle targets are NSIS and MSI, not MSIX. The MSIX is therefore assembled separately from the same SmartPrint.Desktop.exe and the same Agent publish output. The Tauri bundle configuration and existing Inno Setup website installer are not modified.

The package manifest declares:
- `windows.service` for QRtoPrintSmartPrintAgent, running as `localService`;
- `packagedServices` as a restricted capability required by the service extension;
- `runFullTrust` as a restricted capability required by the full-trust Win32/Tauri desktop application;
- `privateNetworkClientServer` for the selected shop LAN;
- an all-profile inbound TCP 17842 firewall exception for Agent\SmartPrint.Agent.exe; Agent middleware continues to restrict remote clients to the selected interface and subnet;
- no `localSystemServices`, no `internetClient`, and no `internetClientServer`.

The desktop app is a full-trust Win32 process, not an AppContainer app. Microsoft documents that `internetClient` is unnecessary for medium-integrity full-trust desktop apps because they already have the user's network access. The Agent's cloud HTTPS requests therefore do not justify declaring that extra capability.

The package minimum OS is Windows 10 version 2004 (build 19041), matching packaged-service support. This minimum version does not, by itself, guarantee that WebView2 is present on every Windows edition or managed device.

The Agent appsettings.json is generated before packaging because MSIX contents are immutable. The API origin is embedded from release build configuration; no post-install edit to the package directory is attempted.

## Website EXE and Store MSIX coexistence/migration

The website EXE and Store MSIX are alternative distributions, not side-by-side installations. They intentionally use the same Windows service name (`QRtoPrintSmartPrintAgent`), TCP port 17842, named pipe and machine-wide `%ProgramData%\QRtoPrint` data. Installing both at once risks a service-registration collision and competing Agent instances.

Switching distribution must be performed in this order:
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

The WebView2 runtime is preinstalled on Windows 11 and many eligible Windows 10 devices, but Microsoft notes that some devices may not have it. The current MSIX package does not run the existing EXE installer's WebView2 prerequisite step. Before release, either:
- define and test a supported Windows edition/version matrix that guarantees the Evergreen Runtime; and verify first launch on clean machines, or
- implement a supported way to provide the runtime without compromising Store packaging or customer installation.

Do not assume that the presence of Microsoft Edge browser alone proves the WebView2 Runtime is available.

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

The current Microsoft Store Submission API workflow in this repository is specifically for MSI/EXE packages. Do not submit an MSIX by changing packageType or reusing the EXE package URL. Upload the .msix through the MSIX package submission flow in Partner Center unless Microsoft provides a supported MSIX submission API for this workflow. The same validation workflow has a manual Store-submission build path: supply the exact Partner Center package Name, Publisher, app display name, publisher display name and four-part version. It creates a temporary self-signed certificate whose subject matches the Partner Center Publisher, signs and locally tests the package, then uploads the MSIX plus a public .cer for optional local sideload testing. Upload only the .msix to Partner Center; do not upload the .cer. Microsoft re-signs the package after certification. This path does not require the website EXE's code-signing PFX and does not submit the package automatically.

## Release status

A package build or successful local installation is not release approval. Release readiness requires automated checks, physical printing parity, WebView2 availability, a canonical public privacy-policy URL for this Win32/Desktop Bridge app, Microsoft restricted-capability approval, package identity/publisher validation and Partner Center certification. If any of those fail, keep the existing website EXE available and do not advertise the MSIX as ready.
