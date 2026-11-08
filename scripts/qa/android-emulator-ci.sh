#!/usr/bin/env bash

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  cat <<'HELP'
Usage: bash scripts/qa/android-emulator-ci.sh [--help|-h]

Runs the complete Android emulator CI sequence; it has no test-selection
flags. It wakes/unlocks the already-created emulator, runs the connected
Android Espresso UI suite, builds APK N and N+1, then runs the app install,
reinstall, upgrade, downgrade-rejection, signing-mismatch, uninstall and
published-release upgrade lifecycle checks.

Required environment:
  SMARTPRINT_PUBLIC_BASE_URL  Canonical HTTPS origin for the local QA stack.
  RUNNER_TEMP                 Writable temporary directory for APK fixtures.
  ANDROID_HOME or ANDROID_SDK_ROOT, adb, Android SDK 36/build tools 36.0.0,
  Gradle 8.13, and one already-booted Android API 36 emulator.

Run this on a disposable CI emulator. The lifecycle helper uninstalls and
reinstalls com.quantaedge.smartprint and tests production release APK updates;
never run the lifecycle helper on a personal/shop phone with user data.

Photo Studio note: the WebView instrumentation requires Gradle properties
smartPrintLocalQa=true and smartPrintPhotoStudioQaEnabled=true. The CI entry
point currently invokes the default connectedDebugAndroidTest build, where
those Photo Studio QA-only tests can be reported as ignored. Check the JUnit
XML report; a green Gradle task alone does not mean ignored tests ran.
HELP
  exit 0
fi
if [[ $# -ne 0 ]]; then
  echo "Usage: bash scripts/qa/android-emulator-ci.sh [--help|-h]" >&2
  exit 2
fi
# Run the Android emulator checks in one process. The emulator-runner action
# executes individual script lines in separate shells, so state such as exit
# codes must not be held in variables in its inline script.
set -euo pipefail

: "${SMARTPRINT_PUBLIC_BASE_URL:?SMARTPRINT_PUBLIC_BASE_URL must be configured}"
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"

echo "[QA-STEP-START] Android emulator readiness | waking, unlocking and preparing device state before instrumentation."
adb wait-for-device
adb shell input keyevent KEYCODE_WAKEUP
adb shell wm dismiss-keyguard || true
adb shell locksettings set-disabled true || true
adb shell settings put global stay_on_while_plugged_in 7
adb shell settings put system screen_off_timeout 2147483647
adb shell input keyevent KEYCODE_HOME
sleep 2

echo "[QA-STEP-CHECK] Android emulator power, display and foreground focus..."

adb shell dumpsys power | grep -E 'mWakefulness=Awake|Display Power: state=ON'
echo "Current Android window focus:"
adb shell dumpsys window | grep -E 'mCurrentFocus|mFocusedApp' || true

# Keep the pre-existing UI suite independent from the lifecycle checks:
# even if Espresso fails, still execute the package-manager scenarios.
ui_test_status=0
echo "[QA-STEP-START] Android Espresso instrumentation | running connected device UI tests with live Gradle detail."
if gradle --no-daemon --console=plain --info -p apps/android -PsmartPrintServiceUrl="$SMARTPRINT_PUBLIC_BASE_URL" :app:connectedDebugAndroidTest; then
  ui_test_status=0
else
  ui_test_status=$?
fi

echo "[QA-STEP-END] Android Espresso instrumentation | exit=$ui_test_status"
echo "[QA-STEP-START] Android package lifecycle | building isolated APK N for install/upgrade validation..."
gradle --no-daemon --console=plain -p apps/android -PsmartPrintServiceUrl="$SMARTPRINT_PUBLIC_BASE_URL" -PsmartPrintVersionCode=9100 -PsmartPrintVersionName="lifecycle-n" :app:assembleDebug
cp apps/android/app/build/outputs/apk/debug/app-debug.apk "$RUNNER_TEMP/smartprint-lifecycle-n.apk"

echo "[QA-STEP-END] Android APK N build | complete"
echo "[QA-STEP-START] Android package lifecycle | building APK N+1 with the same QA signing identity..."
gradle --no-daemon --console=plain -p apps/android -PsmartPrintServiceUrl="$SMARTPRINT_PUBLIC_BASE_URL" -PsmartPrintVersionCode=9101 -PsmartPrintVersionName="lifecycle-n1" :app:assembleDebug
cp apps/android/app/build/outputs/apk/debug/app-debug.apk "$RUNNER_TEMP/smartprint-lifecycle-n1.apk"

echo "[QA-STEP-END] Android APK N+1 build | complete"
echo "[QA-STEP-START] Android package install/upgrade/downgrade lifecycle | executing isolated package manager scenarios..."
if bash scripts/qa/android-install-lifecycle.sh "$RUNNER_TEMP/smartprint-lifecycle-n.apk" "$RUNNER_TEMP/smartprint-lifecycle-n1.apk"; then
  lifecycle_status=0
else
  lifecycle_status=$?
fi

if [ "$ui_test_status" -ne 0 ]; then
  echo "::error::The Android emulator UI suite failed (exit $ui_test_status)."
fi
if [ "$lifecycle_status" -ne 0 ]; then
  echo "::error::Android package installation lifecycle checks failed (exit $lifecycle_status)."
fi
if [ "$ui_test_status" -ne 0 ] || [ "$lifecycle_status" -ne 0 ]; then
  exit 1
fi

echo "Android emulator UI and installation lifecycle checks passed."
