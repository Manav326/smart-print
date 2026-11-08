# SmartPrint QA execution policy

This is a standing project instruction. Apply it to Photo Studio, print/PDF workflows, the Android app, Windows desktop/Agent, and related QA work.

## Required order

1. Inspect the current connected repository and the exact implementation. Do not ask the owner to upload a ZIP when repository access is already available.
2. Execute automated tests on GitHub-hosted runners first. Use all applicable Linux/Chromium, Windows, and Android emulator jobs.
3. Add feature-specific runner coverage where generic platform tests do not exercise the feature. A web browser test is not proof of native Android WebView behaviour; an Android unit test is not proof of Android UI behaviour; a Windows build is not proof of Windows runtime behaviour.
4. Do not ask the owner to run local acceptance checks until the applicable GitHub runner jobs have passed on the exact commit under review.
5. After CI is green, provide the owner with the small set of local acceptance checks that require their own machine, printer, account or device.

## Local owner-run entry point

Use the repository's PowerShell runner from the repository root:

```powershell
.\scripts\qa\photo-studio-test.ps1 -Help
.\scripts\qa\photo-studio-test.ps1
```

The default run installs locked portal dependencies, executes the portal regression suite and Chromium Photo Studio acceptance flow, validates/builds the Windows desktop host and runs Windows API/Agent tests, and runs Android JVM tests plus a debug APK build. It does not connect to a physical printer or submit real payments.

Optional local variants:

```powershell
.\scripts\qa\photo-studio-test.ps1 -Platform Web -Headed
.\scripts\qa\photo-studio-test.ps1 -Platform Windows -UseVirtualPdfPrinter
.\scripts\qa\photo-studio-test.ps1 -Platform Android -RunAndroidEmulator
```

`-RunAndroidEmulator` requires an online Android emulator and runs the shared Photo Studio inside SmartPrint's actual Android WebView. `-UseVirtualPdfPrinter` opts into the Windows **Microsoft Print to PDF virtual queue only**; it never selects a physical printer. The browser runner writes screenshots, a structured summary, and failure details under the selected artifact directory.

## Mixed-file batch page rejection and refund acceptance

Run the isolated workflow with:

```powershell
.\scripts\qa\mixed-batch-refund-test.ps1 -Help
.\scripts\qa\mixed-batch-refund-test.ps1 -Platform All
```

The case uses one verified batch payment containing a three-page PDF, one JPEG and one PNG. It verifies undo before recording, rejects PDF pages 2 and 3 and the page in each image file, records exact per-file page refunds, checks the three-entry refund audit and retries idempotently without duplicating the refund. Only PDF page 1 remains paid and eligible. The fixture totals are ₹50.00 paid, ₹40.00 returned for rejected pages and ₹10.00 retained for the accepted page.

GitHub CI runs the portal flow on Linux/Chromium, the desktop-entry IPC contract test on Windows/Chromium, and a separate mixed-batch run inside the actual native Windows Tauri/WebView2 window. The native test enables WebView2 CDP only on a localhost test port, uses the real Tauri secure-session command, saves and restores the pre-existing Windows Credential Manager session, and intercepts API calls to in-memory QA fixtures on the local Vite origin. The release executable is separately smoke-launched and packaged. A Windows Agent PDF test verifies rejected source pages are excluded from output. No real transfer, live payment, printer dispatch or physical output occurs.

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

- Ubuntu 24.04: portal regression tests, Photo Studio layout/PDF tests, Chromium customer/shop-owner browser acceptance, pixel-level page-image checks, typecheck and build.
- Windows: API tests, Windows Print Agent tests with the Microsoft Print to PDF virtual queue, Windows desktop compilation, Windows Chromium desktop-entry and actual native WebView2 mixed-batch refund acceptance, native WebView2 launch smoke and installer lifecycle validation.
- Android: Android JVM tests, debug APK build, API 36 emulator UI tests, and a dedicated instrumented test that exercises shop-owner and customer Photo Studio flows, native Back dismissal, and A4 PDF content inside MainActivity's real WebView.

**Acceptance gate:** applicable GitHub-hosted runner jobs must pass on the exact commit before requesting the owner's local acceptance run.
