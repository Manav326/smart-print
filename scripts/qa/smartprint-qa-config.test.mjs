import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const config = JSON.parse(readFileSync(new URL('./smartprint-qa.config.json', import.meta.url), 'utf8'));

test('QA configuration uses the supported schema', () => {
  assert.equal(config.schemaVersion, 1);
});

test('public SmartPrint origin is a plain HTTPS origin', () => {
  const value = new URL(config.origins.public);
  assert.equal(value.protocol, 'https:');
  assert.equal(value.pathname, '/');
  assert.equal(value.username, '');
  assert.equal(value.password, '');
  assert.equal(value.search, '');
  assert.equal(value.hash, '');
});

test('local portal origin is loopback-only HTTP', () => {
  const value = new URL(config.origins.local);
  assert.equal(value.protocol, 'http:');
  assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(value.hostname));
  assert.equal(value.pathname, '/');
  assert.equal(value.username, '');
  assert.equal(value.password, '');
  assert.equal(value.search, '');
  assert.equal(value.hash, '');
});

test('Android emulator origin uses the emulator host', () => {
  const value = new URL(config.origins.androidEmulator);
  assert.equal(value.protocol, 'http:');
  assert.equal(value.hostname, '10.0.2.2');
  assert.equal(value.pathname, '/');
});

test('Compose project identity is valid and centralised', () => {
  assert.match(config.composeProjectName, /^[a-z0-9][a-z0-9_-]{1,62}$/);
});

test('runtime requirements are pinned in one place', () => {
  assert.match(config.runtime.minimumNodeVersion, /^\d+\.\d+\.\d+$/);
  assert.match(config.runtime.playwrightVersion, /^\d+\.\d+\.\d+$/);
  assert.ok(Number(config.runtime.minimumNodeVersion.split('.')[0]) >= 22);
});

test('release download routes, names and byte limit are configured together', () => {
  assert.equal(config.downloads.statusPath, '/downloads/status');
  assert.equal(config.downloads.windowsPath, '/downloads/windows');
  assert.equal(config.downloads.androidPath, '/downloads/android');
  assert.equal(config.downloads.windowsAssetName, 'SmartPrint-Windows-Setup.exe');
  assert.equal(config.downloads.androidAssetName, 'SmartPrint-Android.apk');
  assert.ok(Number.isSafeInteger(config.downloads.maxTransferBytes));
  assert.ok(config.downloads.maxTransferBytes >= 1024 * 1024);
  assert.ok(config.downloads.maxTransferBytes <= 512 * 1024 * 1024);
});

test('published Windows app identity and smoke limits are centralised', () => {
  assert.equal(config.apps.windows.installDirectoryName, 'SmartPrint');
  assert.equal(config.apps.windows.executableName, 'SmartPrint.Desktop.exe');
  assert.equal(config.apps.windows.agentServiceName, 'QRtoPrintSmartPrintAgent');
  assert.equal(config.apps.windows.programDataDirectoryName, 'QRtoPrint');
  assert.equal(config.apps.windows.agentIdentityFileName, 'agent.identity');
  assert.ok(config.apps.windows.smokeTimeoutSeconds >= 10);
  assert.ok(config.apps.windows.smokeTimeoutSeconds <= 120);
});

test('published Android app identity only accepts emulator serials', () => {
  assert.equal(config.apps.android.applicationId, 'com.quantaedge.smartprint');
  const emulatorSerial = new RegExp(config.apps.android.emulatorSerialPattern);
  assert.ok(emulatorSerial.test('emulator-5554 device'));
  assert.ok(!emulatorSerial.test('R58M123456A device'));
  assert.ok(!emulatorSerial.test('emulator-5554 offline'));
});

test('Compose local CORS fallback matches the central local origin', () => {
  const compose = readFileSync(new URL('../../compose.local.yml', import.meta.url), 'utf8');
  const localOrigin = new URL(config.origins.local).origin;
  const expected = 'Cors__AllowedOrigins__2: "${SMARTPRINT_LOCAL_ORIGIN:-' + localOrigin + '}"';
  assert.ok(compose.includes(expected), 'compose.local.yml CORS fallback must match scripts/qa/smartprint-qa.config.json');
});


const runnerSource = readFileSync(new URL('./run-smartprint-qa.ps1', import.meta.url), 'utf8');
const publishedAppsSource = readFileSync(new URL('./test-published-apps.ps1', import.meta.url), 'utf8');

test('Android-only published app mode bypasses the normal local web/API QA runner', () => {
  assert.match(runnerSource, /\[switch\]\$AndroidAppOnly/);
  assert.match(runnerSource, /if \(\$AndroidAppOnly\)/);
  assert.match(runnerSource, /-AndroidOnly -PublicOrigin/);
  assert.match(runnerSource, /webE2eExecuted = -not \[bool\]\$AndroidAppOnly/);
});

test('Android-only published app mode does not download or run Windows acceptance', () => {
  assert.match(publishedAppsSource, /\[switch\]\$AndroidOnly/);
  assert.match(publishedAppsSource, /if\(\$AndroidOnly\)\{ @\("android"\) \}/);
  assert.match(publishedAppsSource, /if\(\$AndroidOnly\)\{/);
  assert.match(publishedAppsSource, /no Windows installer was downloaded/);
});

test('Android-only acceptance cannot pass when required live app checks are skipped or warn', () => {
  assert.match(publishedAppsSource, /Android-only completion gate/);
  assert.match(publishedAppsSource, /\$requiredAndroidChecks=@\(/);
  assert.match(publishedAppsSource, /\$checkResults\.Count -ne 1 -or \$checkResults\[0\]\.status -ne "PASS"/);
  assert.match(publishedAppsSource, /A skipped or warning result is not a successful app QA run/);
});
test('Android-only published app mode supports explicit physical-device serial targeting', () => {
  assert.match(runnerSource, /\[string\]\$AndroidDeviceSerial/);
  assert.match(runnerSource, /-AndroidDeviceSerial \$AndroidDeviceSerial/);
  assert.match(publishedAppsSource, /\[string\]\$AndroidDeviceSerial/);
  assert.match(publishedAppsSource, /physical phone\. Supply -AndroidDeviceSerial/);
  assert.match(publishedAppsSource, /-ceq \$DeviceSerial/);
});

test('Android live acceptance verifies foreground launch and native Home/Files navigation', () => {
  assert.match(publishedAppsSource, /uiautomator dump --compressed/);
  assert.match(publishedAppsSource, /Android native UI rendered/);
  assert.match(publishedAppsSource, /Android native Files navigation/);
  assert.match(publishedAppsSource, /Android native Home navigation/);
  assert.match(publishedAppsSource, /Android app crash-log probe/);
  assert.match(publishedAppsSource, /installationReplaced=\$false/);
});

test('Android-only completion gate requires artifact verification and all live app checks', () => {
  assert.match(publishedAppsSource, /\$requiredAndroidChecks=@\(/);
  assert.match(publishedAppsSource, /Verify Android APK application ID/);
  assert.match(publishedAppsSource, /Android native Files navigation/);
  assert.match(publishedAppsSource, /Android app crash-log probe/);
  assert.match(publishedAppsSource, /A skipped or warning result is not a successful app QA run/);
});


test('Android published app launch uses the exact resolved ActivityManager component', () => {
  assert.match(publishedAppsSource, /shell am start -W --user \$installedInfo\.currentUser -n \$launcherComponent/);
  assert.match(publishedAppsSource, /Status:\\s\*ok/);
  assert.match(publishedAppsSource, /launchComponent=\$launcherComponent/);
  assert.doesNotMatch(publishedAppsSource, /shell monkey -p \$package/);
});

test('Android live smoke timeouts and crash-log bounds are centrally configured', () => {
  assert.equal(config.apps.android.launchTimeoutSeconds, 25);
  assert.equal(config.apps.android.uiNavigationTimeoutSeconds, 10);
  assert.equal(config.apps.android.crashLogLineCount, 300);
  assert.match(runnerSource, /timeoutName in @\("launchTimeoutSeconds","uiNavigationTimeoutSeconds"\)/);
  assert.match(runnerSource, /crashLogLineCount -lt 50/);
});

test('Android dependent checks are skipped after launch failure instead of multiplying the root failure', () => {
  assert.match(publishedAppsSource, /Add-AndroidLaunchDependentSkips/);
  assert.match(publishedAppsSource, /Blocked because the Android app did not launch successfully/);
  assert.match(publishedAppsSource, /\$alreadyFailed\.Count -eq 0/);
});


test('published Android APK identity, version and launcher metadata are all verified', () => {
  assert.match(publishedAppsSource, /launchable-activity:/);
  assert.match(publishedAppsSource, /versionCode=/);
  assert.match(publishedAppsSource, /versionName=/);
  assert.match(publishedAppsSource, /Verify Android APK build metadata/);
  assert.match(publishedAppsSource, /Verify Android APK launcher activity/);
});

test('installed Android package preflight is read-only unless explicit update consent is supplied', () => {
  assert.match(publishedAppsSource, /dumpsys package \$Package/);
  assert.match(publishedAppsSource, /cmd package resolve-activity/);
  assert.match(publishedAppsSource, /Android installed package matches published APK and launcher/);
  assert.match(publishedAppsSource, /No uninstall or data clear was attempted/);
  assert.doesNotMatch(publishedAppsSource, /shell pm clear|adb\s+-s\s+\$serial\s+uninstall/);
});


test("Android published APK updates require explicit opt-in and never use uninstall or data-clear fallbacks", () => {
  assert.match(runnerSource, /\[switch\]\$InstallPublishedAndroidApk/);
  assert.match(runnerSource, /-InstallPublishedAndroidApk:\$InstallPublishedAndroidApk/);
  assert.match(publishedAppsSource, /if\(\$versionMismatch -and \$AllowInPlaceUpdate\)/);
  assert.match(publishedAppsSource, /install -r \$Apk/);
  assert.match(publishedAppsSource, /uninstallAttempted=\$false/);
  assert.match(publishedAppsSource, /dataClearAttempted=\$false/);
  assert.match(publishedAppsSource, /Android published APK in-place update/);
  assert.doesNotMatch(publishedAppsSource, /shell pm clear|adb\s+-s\s+\$serial\s+uninstall/);
});


test("Android live launch targets the exact Package Manager-resolved component and active user", () => {
  assert.match(publishedAppsSource, /resolve-activity --brief --user \$currentUser/);
  assert.match(publishedAppsSource, /shell am start -W --user \$installedInfo\.currentUser -n \$launcherComponent/);
  assert.match(publishedAppsSource, /launchComponent=\$launcherComponent/);
});


test("Android native UI automation uses stable resource IDs and tolerates UIAutomator visibility-attribute omissions", () => {
  assert.match(publishedAppsSource, /function Test-AndroidUiNodeVisible/);
  assert.match(publishedAppsSource, /visible-to-user/);
  assert.match(publishedAppsSource, /function Find-AndroidUiNodeByResourceId/);
  assert.match(publishedAppsSource, /GetAttribute\("resource-id"\)/);
  assert.match(publishedAppsSource, /uiAutomationIds\.nativeRoot/);
  assert.match(publishedAppsSource, /uiAutomationIds\.homeNavigation/);
  assert.match(publishedAppsSource, /uiAutomationIds\.filesNavigation/);
  assert.doesNotMatch(publishedAppsSource, /GetAttribute\("visible-to-user"\) -ne "true"/);
});


test("Android live smoke handles a restored Print screen and navigates by stable IDs with legacy label fallback", () => {
  assert.match(publishedAppsSource, /saved shop connection can restore the Print\/WebView tab/);
  assert.match(publishedAppsSource, /Find-AndroidUiNode \$ui .*"Home" .*\$package/);
  assert.match(publishedAppsSource, /Find-AndroidUiNode \$ui .*"My Files" .*\$package/);
  assert.match(publishedAppsSource, /Invoke-AndroidUiNavigation/);
  assert.match(publishedAppsSource, /"Create a new folder" \$package .*ExpectedScreenContentDescriptionOnly/);
  assert.match(publishedAppsSource, /"Your space" \$package/);
});


test("central Android automation IDs match app resource IDs", () => {
  const idsSource = readFileSync(new URL('../../apps/android/app/src/main/res/values/ids.xml', import.meta.url), 'utf8');
  for (const id of Object.values(config.apps.android.uiAutomationIds)) {
    assert.ok(idsSource.includes('name="' + id + '"'), 'Missing Android resource ID: ' + id);
  }
  assert.match(runnerSource, /androidAppConfig\.uiAutomationIds/);
});

test("Android instrumented tests assert stable Home and Files landmarks", () => {
  const instrumented = readFileSync(new URL('../../apps/android/app/src/androidTest/java/com/quantaedge/smartprint/MainActivityInstrumentedTest.kt', import.meta.url), 'utf8');
  assert.match(instrumented, /R\.id\.smartprint_home_heading/);
  assert.match(instrumented, /R\.id\.smartprint_files_heading/);
  assert.match(instrumented, /R\.id\.smartprint_nav_home/);
  assert.match(instrumented, /R\.id\.smartprint_nav_files/);
});


test("Android stable resource IDs are preferred, with semantic fallback for the already-published APK", () => {
  assert.match(publishedAppsSource, /function Find-AndroidUiNodeByResourceId/);
  assert.match(publishedAppsSource, /function Find-AndroidUiNodeByLabel/);
  assert.match(publishedAppsSource, /return \(Find-AndroidUiNodeByLabel/);
  assert.match(publishedAppsSource, /Settings\.uiAutomationIds\.homeHeading/);
  assert.match(publishedAppsSource, /Settings\.uiAutomationIds\.filesHeading/);
  assert.equal(config.apps.android.uiAutomationIds.homeHeading, "smartprint_home_heading");
  assert.equal(config.apps.android.uiAutomationIds.filesHeading, "smartprint_files_heading");
});


test("Android native UI can be confirmed on the published legacy APK by two distinct native navigation landmarks", () => {
  assert.match(publishedAppsSource, /if\(-not \$nativeRoot -and \$homeNavigation -and \$filesNavigation -and \$visibleAppNodes\.Count -ge 2\)/);
  assert.match(publishedAppsSource, /verified-native-navigation-fallback/);
  assert.match(publishedAppsSource, /nativeRootConfirmed=\$nativeRootConfirmed/);
  assert.match(publishedAppsSource, /Android native Files navigation/);
  assert.match(publishedAppsSource, /Android native Home navigation/);
});

test("Windows desktop CI injects WebView2 DevTools args through Tauri config rather than relying on environment override", () => {
  assert.equal(config.apps.windows.webViewDevToolsPort, 9237);
  const desktopConfigScript = readFileSync(new URL('./prepare-desktop-smoke-config.ps1', import.meta.url), 'utf8');
  const desktopSmokeScript = readFileSync(new URL('./desktop-open-smoke.ps1', import.meta.url), 'utf8');
  const workflow = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');
  assert.match(desktopConfigScript, /additionalBrowserArgs/);
  assert.match(desktopConfigScript, /--remote-debugging-port=\$debugPort/);
  assert.match(desktopConfigScript, /--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection/);
  assert.match(workflow, /prepare-desktop-smoke-config\.ps1/);
  assert.match(workflow, /production desktop build failed/);
  assert.match(workflow, /CI-only desktop smoke build failed/);
  assert.match(workflow, /CARGO_TARGET_DIR = \$desktopSmokeTarget/);
  assert.match(workflow, /artifacts\/desktop\/SmartPrint\.Desktop\.exe/);
  assert.match(workflow, /smartprint-desktop-smoke-target\/release\/smartprint-desktop\.exe/);
  assert.match(workflow, /tauri build --config "\$desktopSmokeConfig"/);
  assert.match(desktopSmokeScript, /webViewDevToolsPort/);
  assert.doesNotMatch(desktopSmokeScript, /SetEnvironmentVariable\(\$environmentName/);
});


const liveReporterSource = readFileSync(new URL('./live-node-test-reporter.mjs', import.meta.url), 'utf8');
const liveReporterTestsSource = readFileSync(new URL('./live-node-test-reporter.test.mjs', import.meta.url), 'utf8');
const localE2ESource = readFileSync(new URL('./smartprint-local-e2e.mjs', import.meta.url), 'utf8');
const desktopSmokeSource = readFileSync(new URL('./desktop-open-smoke.ps1', import.meta.url), 'utf8');
const installerLifecycleSource = readFileSync(new URL('./windows-installer-lifecycle.ps1', import.meta.url), 'utf8');
const androidLifecycleSource = readFileSync(new URL('./android-install-lifecycle.sh', import.meta.url), 'utf8');
const androidCiSource = readFileSync(new URL('./android-emulator-ci.sh', import.meta.url), 'utf8');
const androidGradleSource = readFileSync(new URL('../../apps/android/app/build.gradle.kts', import.meta.url), 'utf8');
const responsiveBrowserSource = readFileSync(new URL('../../apps/portal/tests/theme-responsive.browser.mjs', import.meta.url), 'utf8');
const ciWorkflowSource = readFileSync(new URL('../../.github/workflows/ci.yml', import.meta.url), 'utf8');

test('local QA runner streams named Node and xUnit test cases and does not stop on the first test failure', () => {
  assert.match(runnerSource, /live-node-test-reporter\.mjs/);
  assert.match(runnerSource, /--test-concurrency=1/);
  assert.match(runnerSource, /xUnit\.ReporterSwitch=verbose/);
  assert.match(runnerSource, /xUnit\.ShowLiveOutput=true/);
  assert.match(runnerSource, /xUnit\.StopOnFail=false/);
  assert.match(runnerSource, /xUnit\.ParallelizeTestCollections=false/);
  assert.match(runnerSource, /\[QA-STEP-START\]/);
  assert.match(runnerSource, /\[QA-STEP-END\]/);
});

test('custom Node reporter announces case starts, outcomes, source locations and suite totals', () => {
  assert.match(liveReporterSource, /test:dequeue/);
  assert.match(liveReporterSource, /test:complete/);
  assert.match(liveReporterSource, /TEST ERROR/);
  assert.match(liveReporterSource, /test:summary/);
  assert.match(liveReporterTestsSource, /announces the exact case before execution/);
  assert.match(liveReporterTestsSource, /identifies failures, errors, skipped tests and run totals/);
});

test('live E2E record output includes case IDs, case names and immediate end statuses', () => {
  assert.match(localE2ESource, /\[QA-CASE-START\]/);
  assert.match(localE2ESource, /caseIds\.join/);
  assert.match(localE2ESource, /\[QA-CASE-END\]/);
});

test('published Windows and Android acceptance announces the active operation and polling stage', () => {
  assert.match(publishedAppsSource, /function Write-QAProgress/);
  assert.match(publishedAppsSource, /Windows installation and Agent service preflight/);
  assert.match(publishedAppsSource, /Android device and installed-package preflight/);
  assert.match(publishedAppsSource, /Android foreground activity/);
});

test('Desktop startup smoke reports native-window and WebView2 readiness polling', () => {
  assert.match(desktopSmokeSource, /\[QA-ACTION\] Launch SmartPrint Desktop/);
  assert.match(desktopSmokeSource, /\[QA-PROGRESS\] Waiting for native window/);
  assert.match(desktopSmokeSource, /React workspace\/sign-in content via Windows UI Automation/);
  assert.match(desktopSmokeSource, /\[QA-CHECK-PASS\]/);
});

test('Windows installer lifecycle reports each case, wait and safe cleanup stage', () => {
  assert.match(installerLifecycleSource, /\[QA-CASE-START\]/);
  assert.match(installerLifecycleSource, /\[QA-CASE-END\]/);
  assert.match(installerLifecycleSource, /\[QA-PROGRESS\] Agent service state/);
  assert.match(installerLifecycleSource, /\[QA-PROGRESS\] Agent local API and identity readiness/);
  assert.match(installerLifecycleSource, /\[QA-CLEANUP\]/);
});

test('Android JVM and emulator test tasks expose per-test and package lifecycle progress', () => {
  assert.match(androidGradleSource, /events\("started", "passed", "skipped", "failed"\)/);
  assert.match(androidCiSource, /\[QA-STEP-START\] Android Espresso instrumentation/);
  assert.match(androidCiSource, /\[QA-STEP-START\] Android package install\/upgrade\/downgrade lifecycle/);
  assert.match(androidLifecycleSource, /\[QA-PROGRESS\] Android emulator boot/);
  assert.match(runnerSource, /--console=plain/);
  assert.match(ciWorkflowSource, /Run Android JVM unit tests/);
});

test('responsive browser visual QA reports each route, viewport, workspace section and Photo Studio case', () => {
  assert.match(responsiveBrowserSource, /\[QA-STEP-START\] Responsive browser QA/);
  assert.match(responsiveBrowserSource, /\[QA-CASE-START\]/);
  assert.match(responsiveBrowserSource, /\[QA-CASE-END\]/);
  assert.match(responsiveBrowserSource, /Photo Studio/);
  assert.match(responsiveBrowserSource, /checking section layout\/contrast/);
  assert.match(ciWorkflowSource, /test-reporter=\.\/scripts\/qa\/live-node-test-reporter\.mjs/);
});


const nativeLocalRunnerSource = readFileSync(new URL('./run-smartprint-native-local.ps1', import.meta.url), 'utf8');
const localNativeOrchestratorSource = readFileSync(new URL('./run-smartprint-qa.ps1', import.meta.url), 'utf8');
const androidBuildConfigSource = readFileSync(new URL('../../apps/android/app/build.gradle.kts', import.meta.url), 'utf8');

test('local native QA application identities and executable names are configured centrally', () => {
  assert.equal(config.apps.windows.nativeExecutableName, 'smartprint-desktop.exe');
  assert.equal(config.apps.android.applicationId, 'com.quantaedge.smartprint');
  assert.equal(config.apps.android.localQaApplicationId, 'com.quantaedge.smartprint.localqa');
  assert.equal(config.apps.android.launcherActivity, 'com.quantaedge.smartprint.MainActivity');
  assert.ok(config.apps.android.localQaApplicationId !== config.apps.android.applicationId);
});

test('Android local QA build uses a distinct application ID and preserves the production identity', () => {
  assert.match(androidBuildConfigSource, /smartPrintLocalQa/);
  assert.match(androidBuildConfigSource, /com\.quantaedge\.smartprint\.localqa/);
  assert.match(androidBuildConfigSource, /com\.quantaedge\.smartprint"/);
  assert.match(androidBuildConfigSource, /applicationId = smartPrintApplicationId/);
  assert.match(nativeLocalRunnerSource, /-PsmartPrintLocalQa=true/);
  assert.match(nativeLocalRunnerSource, /adbCommand\.Source -s \$androidDevice install -r/);
  assert.doesNotMatch(nativeLocalRunnerSource, /shell pm clear/);
  assert.doesNotMatch(nativeLocalRunnerSource, /adbCommand\.Source -s \$androidDevice uninstall/);
});

test('full local QA runner invokes native Desktop and physical Android acceptance without published downloads', () => {
  assert.match(localNativeOrchestratorSource, /run-smartprint-native-local\.ps1/);
  assert.match(localNativeOrchestratorSource, /-LocalAndroidDeviceSerial/);
  assert.match(nativeLocalRunnerSource, /\.env\.local/);
  assert.match(nativeLocalRunnerSource, /PUBLIC_BASE_URL/);
  assert.match(nativeLocalRunnerSource, /health\/ready/);
  assert.match(nativeLocalRunnerSource, /apps\/desktop\/src-tauri\/target\/release/);
  assert.match(nativeLocalRunnerSource, /desktop-open-smoke\.ps1/);
  assert.match(nativeLocalRunnerSource, /WEBVIEW2_USER_DATA_FOLDER/);
  assert.match(nativeLocalRunnerSource, /connectedDebugAndroidTest/);
  assert.match(nativeLocalRunnerSource, /ANDROID_SERIAL/);
  assert.doesNotMatch(nativeLocalRunnerSource, /test-published-apps\.ps1/);
  assert.doesNotMatch(nativeLocalRunnerSource, /downloads\/windows|downloads\/android/);
});

test('native local QA refuses to mutate existing Agent pairing, network and printer configuration', () => {
  assert.match(nativeLocalRunnerSource, /Get-Service -Name \$agentServiceName/);
  assert.match(nativeLocalRunnerSource, /127\.0\.0\.1:17842\/api\/v1\/local\/status/);
  assert.match(nativeLocalRunnerSource, /LOCAL_ENDPOINT_READY/);
  assert.doesNotMatch(nativeLocalRunnerSource, /pair-agent\.ps1/);
  assert.doesNotMatch(nativeLocalRunnerSource, /enroll_local_agent/);
  assert.doesNotMatch(nativeLocalRunnerSource, /select_local_network/);
  assert.doesNotMatch(nativeLocalRunnerSource, /Set-Printer|Add-Printer|Remove-Printer/);
});

test('native local QA refuses to take over an already-running Desktop session', () => {
  assert.match(nativeLocalRunnerSource, /apps\.windows\.nativeExecutableName/);
  assert.match(nativeLocalRunnerSource, /apps\.windows\.executableName/);
  assert.match(nativeLocalRunnerSource, /Get-CimInstance -ClassName Win32_Process -ErrorAction Stop/);
  assert.match(nativeLocalRunnerSource, /desktopProcessNames -contains \$_.Name/);
  assert.match(nativeLocalRunnerSource, /A SmartPrint Desktop session is already running under either the native or installed executable name/);
  assert.match(nativeLocalRunnerSource, /will not take over or close the user's active workspace/);
});
test('native QA logs each named step and produces separate machine-readable evidence', () => {
  assert.match(nativeLocalRunnerSource, /\[QA-STEP-START\]/);
  assert.match(nativeLocalRunnerSource, /\[QA-STEP-END\]/);
  assert.match(nativeLocalRunnerSource, /Tee-Object -FilePath \$logPath/);
  assert.match(nativeLocalRunnerSource, /results\.json/);
  assert.match(nativeLocalRunnerSource, /results\.txt/);
});

test('native local QA propagates failure to the parent runner without terminating its summary/finally block', () => {
  assert.match(nativeLocalRunnerSource, /throw "Local native application QA recorded \$failed failed check\(s\)\./);
  assert.doesNotMatch(nativeLocalRunnerSource, /if \(\$failed -gt 0\) \{ exit 1 \}/);
  assert.match(localNativeOrchestratorSource, /Invoke-QAStep .*Local Windows Desktop native tests and Android side-by-side device acceptance/);
});

test('physical Android QA runs the full existing Espresso instrumentation suite on one explicitly selected device', () => {
  assert.match(nativeLocalRunnerSource, /Run the full Android Espresso instrumentation suite on the selected physical phone/);
  assert.match(nativeLocalRunnerSource, /:app:connectedDebugAndroidTest/);
  assert.match(nativeLocalRunnerSource, /ANDROID_SERIAL/);
  assert.match(nativeLocalRunnerSource, /"--serial", \$androidDevice/);
  assert.match(nativeLocalRunnerSource, /-PsmartPrintLocalQa=true/);
  assert.match(nativeLocalRunnerSource, /-PsmartPrintServiceUrl=\$publicOrigin/);
  assert.match(nativeLocalRunnerSource, /-PsmartPrintVersionCode=\$versionCode/);
  assert.match(nativeLocalRunnerSource, /-PsmartPrintVersionName=\$versionName/);
});
