# SmartPrint local-native application QA

This guide explains the native-app portion of the local production acceptance runner. It does not download or test the latest public Windows installer or public Android APK.

The full local Windows runner now executes the isolated Chromium visual suite for the shared customer/shop Photo Studio: all editor tools, responsive/light-dark layouts, synthetic A4 PDF raster geometry, copy/page arithmetic and injected rendering/retry failures are reported as a dedicated terminal step. It uses synthetic photos and mocked API responses; it does not replace real-data E2E or physical print evidence. After the automated local run, complete the [role-based cross-platform acceptance matrix](ROLE_BASED_CROSS_PLATFORM_QA.md). The native Desktop startup/accessibility smoke is not a full owner-workflow test. Android WebView instrumentation exercises native touch controls, PDF output and secure-window lifecycle when an Android emulator/device test target is available, but APHO-001–036 remain a separate physical-device gate for picker/provider, phone hardware, touch, keyboard, rotation, memory and device-lifecycle scenarios.

## One-command full local run

From the existing Windows checkout:

    cd D:\Projects\QRtoPrint
    git pull --ff-only origin feature/smartprint-clean-qa-runtime
    .\scripts\qa\run-smartprint-qa.ps1

Run it from the same Windows user profile that owns the local QA identities and DPAPI credentials. Connect the Android phone over USB with USB debugging enabled and approve the computer. When exactly one ADB device is online, the runner selects it automatically. When several devices are online, select the intended phone explicitly:

    .\scripts\qa\run-smartprint-qa.ps1 -LocalAndroidDeviceSerial "YOUR_DEVICE_SERIAL"

Find the serial with adb devices. The LocalAndroidDeviceSerial option applies only to the full local suite; it is not the public-release Android-only mode.

## What the full command executes

1. Verifies and builds the local Docker API and Portal, reusing the existing local PostgreSQL/document volumes. It runs the existing browser/API workflows against http://127.0.0.1:3000 and the local database.
2. Runs the portal logic tests and the API/.NET regression suite.
3. Runs the Windows Agent .NET regression suite and Microsoft Print to PDF integration when the real Windows virtual printer is available.
4. Runs Android JVM tests, builds the Android debug APK, and runs the Android emulator instrumentation suite when exactly one emulator is online.
5. Typechecks and builds the native Windows Desktop frontend, runs the Tauri/Rust host tests, builds the Windows executable against the configured local tunnel origin, and launches it with a disposable WebView2 profile for the native-window/accessibility smoke test.
6. Checks the already-installed Windows Agent service and reads its loopback status endpoint without pairing, reconnecting, changing networks or changing printers.
7. When one Android device is selected, builds com.quantaedge.smartprint.localqa, installs/updates only that side-by-side QA package, launches it, checks the native shell, navigates Home → My Files → Home, and checks the current process logs for fatal exceptions.

## Local origin and data safety

The QA public origin in scripts/qa/smartprint-qa.config.json must match PUBLIC_BASE_URL in .env.local. The existing central Cloudflare Tunnel must route that origin to the local SmartPrint portal container. The runner also checks that both the loopback portal and configured tunnel URL report API readiness and PostgreSQL connectivity before building native apps.

The Android QA build uses the local tunnel origin so a physical phone reaches the local Compose stack through the existing tunnel. It has a distinct application ID (com.quantaedge.smartprint.localqa), so Android can keep the existing customer app installed beside it. The runner uses adb install -r for the QA package only. It never uninstalls the original com.quantaedge.smartprint app, never clears its data, and never falls back to uninstalling after a signing or install error.

The Windows Desktop smoke test uses a disposable WebView2 profile under the QA evidence directory. It does not take over an already-running instance of the exact local QA executable and does not touch the installed app's normal WebView profile.

The local runner does not:
- Download public-release Windows or Android packages.
- Use the production database.
- Pair, revoke, reconnect or reconfigure the Agent.
- Change the selected shop Wi-Fi interface or printer configuration.
- Send a test document to a physical printer automatically.
- Enable live payment gateways or submit real payments.

The Windows Agent must already be installed, running and paired. The read-only local readiness check fails rather than silently pairing it. The actual printer model still needs a controlled shop-floor acceptance print; automated tests use the real Microsoft Print to PDF virtual printer where available.

## Live terminal progress and reports

Each native test step prints its start, current action, end, elapsed time and PASS/FAIL/SKIPPED result. The helper also saves per-step logs and a machine-readable JSON summary.

Reports are written below:

    artifacts/smartprint-qa/<run-id>/summary.json
    artifacts/smartprint-qa/<run-id>/logs/
    artifacts/smartprint-qa/<run-id>/native-local/results.json
    artifacts/smartprint-qa/<run-id>/native-local/results.txt
    artifacts/smartprint-qa/<run-id>/native-local/*.log

A skipped physical-device or Desktop-window check is not evidence of a pass. Review the per-test outcomes, connect the missing device or close the already-running test instance safely, and rerun. Keep the Android physical acceptance matrix in docs/ANDROID_PHYSICAL_ACCEPTANCE.md as the manual companion for real camera, file-provider, QR, shop-Wi-Fi and end-to-end printer tests.

## Public-release testing is separate

Do not pass -TestPublishedApps during the local-first run. That separate option intentionally downloads the currently published Windows installer/Android APK from the configured website. Use it only after local acceptance and when explicitly testing the public distribution channel.