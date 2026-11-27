# SmartPrint MSIX — printing parity and release gates

## Non-negotiable requirement

The MSIX distribution is a second Windows package for the existing SmartPrint desktop application. It must not become a UI-only or reduced-functionality edition.

The MSIX package includes the same native Tauri desktop executable and the same self-contained .NET SmartPrint Agent build used by the existing Windows release. The Agent remains a Windows service running as NT AUTHORITY\LocalService; it is not replaced with a per-user background process.

The existing website installer (SmartPrint-Windows-Setup.exe) and its service installer/uninstaller remain independent and unchanged by the MSIX packaging path. Android APK and AAB release paths are also independent.

## Required parity checks

| Area | Required behavior |
|---|---|
| Agent lifecycle | Windows registers QRtoPrintSmartPrintAgent as an automatic packaged service under LocalService; it starts after install and is running after upgrade. SCM crash-recovery parity is a separate unresolved release blocker |
| Desktop IPC | Existing named-pipe status, enrollment and network-selection operations remain available to the desktop UI |
| Local API | Agent continues listening on TCP 17842; existing status, local upload, preview, photo-revision and phone-check endpoints remain in the same Agent binary |
| LAN | Package declares private-network access and an inbound all-profile firewall exception for the Agent on TCP 17842; Agent-side selected-interface/subnet validation remains authoritative and must be tested on Private, Public and Domain profiles |
| Printer discovery | Same WindowsPrinterDiscovery and Windows spooler integration are used |
| Printing | Same queue, worker, PDF rendering and print executor are used; no alternate print implementation is introduced |
| Pairing and identity | Existing encrypted machine identity, enrollment, revocation, DPAPI and device identity behavior remain in the Agent |
| Private printing | Existing encrypted local queue and protected temporary-document handling remain in the Agent |
| Persistence | %ProgramData%\QRtoPrint is outside the package and must survive package upgrade and uninstall |
| WebView2 | Desktop UI uses the installed Evergreen WebView2 Runtime; MSIX setup does not run an installer or download prerequisites during installation |

## Package implementation

Tauri v2's supported Windows bundle targets are NSIS and MSI, not MSIX. The MSIX is therefore assembled separately from the same SmartPrint.Desktop.exe and the same Agent publish output. The Tauri bundle configuration and existing Inno Setup website installer are not modified.

The package manifest declares:
- windows.service for the existing QRtoPrintSmartPrintAgent service, running as localService;
- packagedServices as a restricted capability required by the service extension;
- privateNetworkClientServer for home/work LAN communication;
- a private-profile inbound TCP 17842 firewall exception for Agent\SmartPrint.Agent.exe;
- runFullTrust for the existing Win32 desktop application;
- internetClient for the Agent's outbound cloud communication.

The package minimum OS is Windows 10 version 2004 (build 19041), matching packaged-service support.

The Agent appsettings.json is generated before packaging because MSIX contents are immutable. The API origin is embedded from release build configuration; no post-install edit to the package directory is attempted.

## Automated validation

.github/workflows/msix-validation.yml builds the desktop app and Agent, packages and signs an ephemeral CI MSIX, installs it, and verifies:
1. Windows registers and starts the Agent service as LocalService.
2. The existing named-pipe protocol responds to a status request.
3. The existing local HTTP API responds on TCP 17842.
4. Windows printer subsystem enumeration is available to the same Agent build.
5. A package upgrade preserves %ProgramData%\QRtoPrint and keeps the Agent service running.
6. Package uninstall removes the packaged service but preserves machine data.

The current desktop6:Service manifest schema exposes service name, startup type, start account, arguments and dependencies, but no SCM failure-action settings. The existing EXE installer configures SCM recovery with sc.exe; the current MSIX package does not yet demonstrate equivalent crash recovery. This is a release blocker. Do not claim crash-recovery parity until a Store-compatible method is implemented and a forced Agent-process crash test proves automatic restart.\n\nThis CI validation does not prove physical printer output: a hosted runner does not provide the customer's actual printer, USB/network topology, spooler driver or phone. Before release, test the exact signed MSIX on clean Windows machines with representative USB and network printers, a phone on the selected shop LAN, actual PDF/photo jobs, cancellation, queue recovery, sleep/resume, upgrade and uninstall. Confirm the Windows firewall permits a phone on the selected shop LAN to reach TCP 17842 while the Agent still rejects clients outside the selected network.

## Microsoft Store approval is a separate gate

Microsoft classifies packagedServices as a restricted capability and explicitly warns it is not approved in most Store submissions. Local sideloading and a successful CI install do not grant Store approval.

Before publishing the MSIX:
1. Request approval for packagedServices in Partner Center.
2. Explain that SmartPrint's existing printing architecture requires a LocalService Windows service to discover printers, run the durable print worker, protect local documents and expose the existing LAN endpoint to paired shop devices.
3. Provide parity test evidence and explain why a per-user process would reduce unattended printing, multi-user reliability and access to machine-wide printer queues.
4. Do not submit the package for Store certification until Microsoft has approved the restricted capability and clean-machine/physical-printer acceptance tests pass.

The current Microsoft Store Submission API workflow in this repository is specifically for MSI/EXE packages. Do not submit an MSIX by changing packageType or reusing the EXE package URL. Upload the .msix through the MSIX package submission flow in Partner Center unless Microsoft provides a supported MSIX submission API for this workflow.

## Release status

A package build or successful local installation is not release approval. Release readiness requires automated checks, physical printing parity, Microsoft restricted-capability approval, package identity/publisher validation and Partner Center certification. If any of those fail, keep the existing website EXE available and do not advertise the MSIX as ready.
