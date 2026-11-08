# SmartPrint QA execution policy

This is a standing project instruction. Apply it to Photo Studio, print/PDF workflows, the Android app, Windows desktop/Agent, and related QA work.

## Required order

Use [FINAL_PRE_PRODUCTION_RELEASE_GATE.md](FINAL_PRE_PRODUCTION_RELEASE_GATE.md) as the release go/no-go index; retain the detailed case IDs in the linked master, role-based and physical-device catalogues.

1. Inspect the current connected repository and the exact implementation. Do not ask the owner to upload a ZIP when repository access is already available.
2. Execute automated tests on GitHub-hosted runners first. Use all applicable Linux/Chromium, Windows, and Android emulator jobs.
3. Add feature-specific runner coverage where generic platform tests do not exercise the feature. A web browser test is not proof of native Android WebView behaviour; an Android unit test is not proof of Android UI behaviour; a Windows build is not proof of Windows runtime behaviour.
4. Do not ask the owner to run local acceptance checks until the applicable GitHub runner jobs have passed on the exact commit under review.
5. After CI is green, provide the owner with the small set of local acceptance checks that require their own machine, printer, account or device.

## Local owner-run Photo Studio entry point

Use the repository PowerShell runner from the repository root:

```powershell
.\scripts\qa\photo-studio-test.ps1 -Help
.\scripts\qa\photo-studio-test.ps1
```

The default run executes portal logic and Chromium acceptance, Windows API/Agent tests, and Android JVM tests plus a debug APK build. Optional variants are:

```powershell
.\scripts\qa\photo-studio-test.ps1 -Platform Web -Headed
.\scripts\qa\photo-studio-test.ps1 -Platform Windows -UseVirtualPdfPrinter
.\scripts\qa\photo-studio-test.ps1 -Platform Android -RunAndroidEmulator
```

`-RunAndroidEmulator` runs the shared Photo Studio inside the real Android WebView. `-UseVirtualPdfPrinter` opts into only the Microsoft Print to PDF virtual queue. No real payment or physical printer is used.

## Mixed-file batch page rejection and refund acceptance

Run the dedicated mixed-file scenario:

```powershell
.\scripts\qa\mixed-batch-refund-test.ps1 -Help
.\scripts\qa\mixed-batch-refund-test.ps1 -Platform All
```

The fixture contains one three-page PDF, one JPEG and one PNG under one verified batch payment. It undoes a page rejection before confirmation, rejects PDF pages 2 and 3 and both image files, verifies the exact per-page/file refund allocation, checks the three-entry refund audit and retries idempotently. The expected fixture totals are ₹50.00 paid, ₹40.00 refunded and ₹10.00 retained for the accepted PDF page.

CI exercises the shop workflow in Linux Chromium, the desktop renderer on Windows, and the actual Tauri/WebView2 Windows application. The native test uses a localhost-only CDP port and the real Tauri secure-session bridge, saves and restores the prior Windows Credential Manager session, and intercepts APIs with isolated in-memory fixtures. It does not submit a real payment, transfer a refund, dispatch a print job or produce physical output.

## Evidence and status discipline

- A written test case, source-code inspection, or test file present in the repository is not an executed PASS.
- Record each case as PASS, FAIL, BLOCKED, or NOT RUN.
- Retain the workflow/run URL, tested commit SHA, job results, logs, screenshots, PDFs and Android test reports where available.
- When a change is made, rerun the affected jobs and verify the final tested SHA.
- Do not claim full completion while any required case is blocked, skipped or not run.

## Platform expectations

- **Web/Chromium:** run unit/regression tests and actual browser interactions at phone, tablet and desktop viewports, in light and dark themes.
- **Android:** run JVM/unit tests, build the APK, run instrumentation on the Android emulator, and explicitly test the shared Photo Studio inside the real Android WebView, including file selection/import, editor interaction, PDF generation, Back/background/resume and narrow-screen layout.
- **Windows:** run API/Agent tests, the actual Microsoft Print to PDF integration, native desktop compilation and the Windows desktop/browser Photo Studio acceptance flow. A successful compile alone is insufficient.
- **PDF output:** verify physical A4 page geometry, actual photo dimensions, placement/margins/gaps, output page count, final partially filled page, PDF structure/openability, image content and the requested-copy count. A PDF page-count string assertion alone is insufficient.
- **Shop-owner flow:** verify the shop owner can open the correct uploaded photo/job, edit it, save the prepared PDF back to the same existing job, inspect the output, and preserve the original/job identity. Do not treat customer-mode tests as proof of shop-mode behaviour.

## Production and data safety

- Prefer feature-branch or pull-request CI to test changes.
- Never push to `main` just to start CI when that workflow can publish containers, create releases, or otherwise affect production.
- Use isolated synthetic QA data and non-production services for destructive or state-changing tests.
- Do not submit real payments, dispatch physical print jobs, or mutate production records as test setup.

## Current runner inventory verified in `.github/workflows/ci.yml`

- Ubuntu 24.04: portal regression tests, dedicated Photo Studio browser acceptance, mixed-type batch page rejection/refund browser acceptance, responsive/theme suite, typecheck and build.
- Windows: API/Agent tests and Microsoft Print to PDF integration; Photo Studio browser acceptance; mixed-batch refund acceptance in the desktop renderer and actual native Tauri/WebView2 host; Windows desktop compilation, smoke launch and installer lifecycle validation.
- Android: Android unit tests, debug APK build, and API 36 emulator UI tests.

**Acceptance gate:** applicable GitHub-hosted runner jobs must pass on the exact commit before requesting the owner's local acceptance run.
