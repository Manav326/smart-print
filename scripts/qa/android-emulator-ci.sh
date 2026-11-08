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

Photo Studio instrumentation uses the QA-only Vite build containing
tests/photo-studio.browser.html. The runner uses the side-by-side package ID
com.quantaedge.smartprint.localqa and enables smartPrintPhotoStudioQaEnabled.
The emulator reaches the fixture at http://10.0.2.2:3000; Android debug WebView
allows this exact private-LAN development host/port. The normal Portal build
separately asserts that the QA route is absent. After instrumentation, the
runner parses JUnit XML and fails if any of the four Photo Studio cases is
missing, skipped, failed or duplicated. The later install-lifecycle APKs use
the normal customer application ID and HTTPS public-origin setting.
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
photo_studio_qa_origin="${SMARTPRINT_PHOTOSTUDIO_QA_BASE_URL:-http://10.0.2.2:3000}"
case "$photo_studio_qa_origin" in
  http://10.0.2.2:3000) ;;
  *)
    echo "::error::Photo Studio WebView QA must use the isolated emulator fixture at http://10.0.2.2:3000."
    exit 2
    ;;
esac

if gradle --no-daemon --console=plain --info -p apps/android \
  -PsmartPrintServiceUrl="$photo_studio_qa_origin" \
  -PsmartPrintLocalQa=true \
  -PsmartPrintPhotoStudioQaEnabled=true \
  :app:connectedDebugAndroidTest; then
  ui_test_status=0
else
  ui_test_status=$?
fi

echo "[QA-STEP-END] Android Espresso instrumentation | exit=$ui_test_status"

# Gradle can return success when JUnit Assumptions ignore the Photo Studio
# tests. Require all four exact cases to exist and execute with no skip/failure.
if [ "$ui_test_status" -eq 0 ]; then
  echo "[QA-STEP-START] Android Photo Studio coverage gate | parsing connected instrumentation JUnit XML."
  if python - <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

report_dir = Path("apps/android/app/build/outputs/androidTest-results/connected/debug")
reports = list(report_dir.glob("TEST-*.xml"))
if len(reports) != 1:
    raise SystemExit(f"Expected exactly one connected Android JUnit XML report, found {len(reports)} in {report_dir}")

expected = {
    "customerPhotoStudioRunsInsideAndroidWebViewAndProducesExactMultiPagePdf",
    "shopOwnerCanEditAnExistingPhotoJobInsideAndroidWebViewAndSaveReplacementPdf",
    "androidSystemBackClosesPhotoStudioBeforeLeavingTheCustomerStation",
    "photoStudioModalStateAndSecureFlagSurviveActivityRecreation",
}
root = ET.parse(reports[0]).getroot()
matches = [
    case for case in root.iter("testcase")
    if case.attrib.get("classname") == "com.quantaedge.smartprint.PhotoStudioWebViewInstrumentedTest"
]
names = [case.attrib.get("name", "") for case in matches]
missing = sorted(expected - set(names))
duplicates = sorted(name for name in expected if names.count(name) != 1)
skipped = sorted(case.attrib.get("name", "") for case in matches if case.find("skipped") is not None)
failed = sorted(case.attrib.get("name", "") for case in matches if case.find("failure") is not None or case.find("error") is not None)

print(f"Android Photo Studio WebView cases: total={len(matches)} expected=4")
for case in matches:
    name = case.attrib.get("name", "<unnamed>")
    status = "SKIP" if case.find("skipped") is not None else "FAIL" if case.find("failure") is not None or case.find("error") is not None else "PASS"
    print(f"[ANDROID-PHOTOSTUDIO] {status} | {name}")

problems = []
if len(matches) != 4: problems.append(f"expected exactly 4 Photo Studio cases, found {len(matches)}")
if missing: problems.append("missing cases: " + ", ".join(missing))
if duplicates: problems.append("missing/duplicated exact-one cases: " + ", ".join(duplicates))
if skipped: problems.append("ignored cases: " + ", ".join(skipped))
if failed: problems.append("failed cases: " + ", ".join(failed))
if problems:
    for problem in problems:
        print("::error::" + problem, file=sys.stderr)
    raise SystemExit(1)
print("PASS: all four Android Photo Studio WebView tests executed and passed.")
PY
  then
    photostudio_gate_status=0
  else
    photostudio_gate_status=$?
  fi
else
  photostudio_gate_status=1
fi

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
if [ "$ui_test_status" -ne 0 ] || [ "$lifecycle_status" -ne 0 ] || [ "$photostudio_gate_status" -ne 0 ]; then
  exit 1
fi

echo "Android emulator UI and installation lifecycle checks passed."
