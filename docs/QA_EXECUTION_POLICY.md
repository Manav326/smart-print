# SmartPrint QA execution policy

This is a standing project instruction. Apply it to Photo Studio, print/PDF workflows, the Android app, Windows desktop/Agent, and related QA work.

## Required order

1. Inspect the current connected repository and the exact implementation. Do not ask the owner to upload a ZIP when repository access is already available.
2. Execute automated tests on GitHub-hosted runners first. Use all applicable Linux/Chromium, Windows, and Android emulator jobs.
3. Add feature-specific runner coverage where generic platform tests do not exercise the feature. A web browser test is not proof of native Android WebView behaviour; an Android unit test is not proof of Android UI behaviour; a Windows build is not proof of Windows runtime behaviour.
4. Do not ask the owner to run local acceptance checks until the applicable GitHub runner jobs have passed on the exact commit under review.
5. After CI is green, provide the owner with the small set of local acceptance checks that require their own machine, printer, account or device.

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

- Ubuntu 24.04: portal tests, Photo Studio unit tests, Chromium responsive/browser flow, typecheck and build.
- Windows: API tests, Windows Print Agent tests with the actual Microsoft Print to PDF queue, Windows desktop compilation and installer lifecycle validation.
- Android: Android unit tests, debug APK build, and API 36 emulator UI tests.

**Acceptance gate:** applicable GitHub-hosted runner jobs must pass on the exact commit before requesting the owner's local acceptance run.
