#!/usr/bin/env bash
set -euo pipefail

PACKAGE="com.quantaedge.smartprint"
BUILD_TOOLS="$ANDROID_HOME/build-tools/36.0.0"
APKSIGNER="$BUILD_TOOLS/apksigner"
AAPT="$BUILD_TOOLS/aapt"
DEBUG_V1="$1"
DEBUG_V2="$2"
RUNNER_TEMP="$(printenv RUNNER_TEMP || echo /tmp)"
SENTINEL="SmartPrint private data must survive in-place updates."
PRIVATE_PATH="files/documents/Lifecycle/sentinel.txt"
EXTERNAL_DIR="/sdcard/Download/SmartPrintLifecycleQA"
EXTERNAL_PATH="$EXTERNAL_DIR/exported-document.txt"
KEYSTORE="$RUNNER_TEMP/smartprint-lifecycle-mismatch.jks"
MISMATCH_APK="$RUNNER_TEMP/smartprint-lifecycle-mismatch.apk"
PROD_V1="$RUNNER_TEMP/smartprint-production-v0.1.9.apk"
PROD_V2="$RUNNER_TEMP/smartprint-production-v0.1.10.apk"

fail() {
  echo "::error::$1"
  exit 1
}

require_file() {
  [[ -s "$1" ]] || fail "Required APK or tool is missing: $1"
}

private_file_matches() {
  local actual
  actual="$(adb exec-out run-as "$PACKAGE" cat "$PRIVATE_PATH" | tr -d '\r')"
  [[ "$actual" == "$SENTINEL" ]] || fail "The app-private document was not preserved intact."
}

write_private_file() {
  local encoded
  encoded="$(printf '%s' "$SENTINEL" | base64 | tr -d '\n')"
  # Keep the redirection inside the app-UID shell, not adb's outer shell.
  adb shell "run-as $PACKAGE sh -c 'printf %s $encoded | base64 -d > $PRIVATE_PATH'"
}


apk_version_code() {
  "$AAPT" dump badging "$1" | sed -nE "s/^package:.*versionCode='([0-9]+)'.*/\1/p" | head -n 1
}

apk_certificate() {
  "$APKSIGNER" verify --verbose --print-certs "$1" 2>&1 |
    sed -nE "s/.*certificate SHA-256 digest: *([[:xdigit:]]{64}).*/\1/p" |
    head -n 1 |
    tr '[:upper:]' '[:lower:]'
}

echo "[QA-STEP-START] Android package lifecycle | wait for the disposable emulator to boot and respond."
adb wait-for-device
for attempt in $(seq 1 30); do
  if [[ "$(adb shell getprop sys.boot_completed | tr -d '\r')" == "1" ]]; then
    echo "[QA-STEP-END] Android emulator boot complete | attempts=$attempt"
    break
  fi
  if (( attempt == 1 || attempt % 5 == 0 )); then
    echo "[QA-PROGRESS] Android emulator boot | attempt=$attempt/30 | waited=$((attempt * 2))s"
  fi
  sleep 2
done
[[ "$(adb shell getprop sys.boot_completed | tr -d '\r')" == "1" ]] || fail "Android emulator did not finish booting."
require_file "$DEBUG_V1"
require_file "$DEBUG_V2"
require_file "$APKSIGNER"
require_file "$AAPT"

adb uninstall "$PACKAGE" >/dev/null 2>&1 || true

echo "1/8 Installing lifecycle APK N..."
adb install "$DEBUG_V1"
adb shell run-as "$PACKAGE" mkdir -p files/documents/Lifecycle
write_private_file
EXTERNAL_FIXTURE="$RUNNER_TEMP/smartprint-lifecycle-exported-document.txt"
printf '%s' 'Exported files should remain after app removal.' > "$EXTERNAL_FIXTURE"
adb shell mkdir -p "$EXTERNAL_DIR"
adb push "$EXTERNAL_FIXTURE" "$EXTERNAL_PATH" >/dev/null
private_file_matches
echo "PASS: Fresh install created app-private and exported-file fixtures."

echo "2/8 Reinstalling the exact same APK..."
adb install -r "$DEBUG_V1"
private_file_matches
adb shell test -f "$EXTERNAL_PATH"
echo "PASS: Same-version reinstall preserved private and exported files."

echo "3/8 Installing the higher-version APK over the existing app..."
adb install -r "$DEBUG_V2"
private_file_matches
adb shell test -f "$EXTERNAL_PATH"
echo "PASS: In-place N to N+1 update preserved private and exported files."

echo "4/8 Confirming downgrade is rejected..."
set +e
downgrade_output="$(adb install -r "$DEBUG_V1" 2>&1)"
downgrade_status=$?
set -e
if [[ $downgrade_status -eq 0 ]] || ! grep -q "INSTALL_FAILED_VERSION_DOWNGRADE" <<<"$downgrade_output"; then
  printf '%s\n' "$downgrade_output"
  fail "Android did not reject the lower versionCode as expected."
fi
private_file_matches
echo "PASS: Downgrade was rejected and the installed app/data remained intact."

echo "5/8 Confirming a different signing identity cannot replace the installed app..."
keytool -genkeypair -noprompt \
  -keystore "$KEYSTORE" \
  -storepass "Lifecycle-Test-Only-42!" \
  -keypass "Lifecycle-Test-Only-42!" \
  -alias "lifecycle-mismatch" \
  -keyalg RSA -keysize 2048 -validity 3650 \
  -dname "CN=SmartPrint Lifecycle Test Mismatch" >/dev/null 2>&1
"$APKSIGNER" sign \
  --ks "$KEYSTORE" \
  --ks-key-alias "lifecycle-mismatch" \
  --ks-pass pass:Lifecycle-Test-Only-42! \
  --key-pass pass:Lifecycle-Test-Only-42! \
  --out "$MISMATCH_APK" "$DEBUG_V2"
set +e
mismatch_output="$(adb install -r "$MISMATCH_APK" 2>&1)"
mismatch_status=$?
set -e
if [[ $mismatch_status -eq 0 ]] || ! grep -q "INSTALL_FAILED_UPDATE_INCOMPATIBLE" <<<"$mismatch_output"; then
  printf '%s\n' "$mismatch_output"
  fail "Android did not reject the APK signed by a different certificate."
fi
private_file_matches
echo "PASS: Signature mismatch was rejected without replacing the installed app."

echo "6/8 Confirming uninstall removes app-private data but retains exported files..."
adb uninstall "$PACKAGE"
if adb shell run-as "$PACKAGE" test -f "$PRIVATE_PATH" >/dev/null 2>&1; then
  fail "App-private lifecycle data still exists after uninstall."
fi
adb shell test -f "$EXTERNAL_PATH" || fail "The exported shared-storage fixture was removed with the app."
echo "PASS: Uninstall removed app-private data and retained the exported file."

echo "7/8 Confirming fresh reinstall does not restore old private data..."
adb install "$DEBUG_V2"
if adb shell run-as "$PACKAGE" test -f "$PRIVATE_PATH" >/dev/null 2>&1; then
  fail "Fresh installation unexpectedly retained old private data."
fi
adb shell test -f "$EXTERNAL_PATH" || fail "The exported shared-storage fixture disappeared after reinstall."
echo "PASS: Fresh install starts with empty private data; exported file remains."

# This is the customer app, not a shop-bound device. The production update
# check protects customer-owned documents only; shop sessions remain scoped by
# station code so the customer can switch to another shop at any time.
echo "8/8 Testing two real production-signed releases..."
curl --fail --location --silent --show-error \
  "https://github.com/Manav326/QRtoPrint/releases/download/v0.1.9/SmartPrint-Android.apk" \
  --output "$PROD_V1"
curl --fail --location --silent --show-error \
  "https://github.com/Manav326/QRtoPrint/releases/download/v0.1.10/SmartPrint-Android.apk" \
  --output "$PROD_V2"
require_file "$PROD_V1"
require_file "$PROD_V2"
prod_cert_v1="$(apk_certificate "$PROD_V1")"
prod_cert_v2="$(apk_certificate "$PROD_V2")"
[[ -n "$prod_cert_v1" && "$prod_cert_v1" == "$prod_cert_v2" ]] ||
  fail "Production APK signing certificate changed between v0.1.9 and v0.1.10."
prod_code_v1="$(apk_version_code "$PROD_V1")"
prod_code_v2="$(apk_version_code "$PROD_V2")"
[[ "$prod_code_v1" =~ ^[0-9]+$ && "$prod_code_v2" =~ ^[0-9]+$ ]] ||
  fail "Could not read production APK version codes."
(( prod_code_v2 > prod_code_v1 )) ||
  fail "Production versionCode did not increase from v0.1.9 to v0.1.10."

adb uninstall "$PACKAGE" >/dev/null 2>&1 || true
adb install "$PROD_V1"
adb install -r "$PROD_V1"
adb install -r "$PROD_V2"
installed_code="$(adb shell dumpsys package "$PACKAGE" | sed -nE 's/.*versionCode=([0-9]+).*/\1/p' | head -n 1 | tr -d '\r')"
[[ "$installed_code" == "$prod_code_v2" ]] ||
  fail "Production APK upgrade did not leave v0.1.10 installed."
echo "PASS: Published production APKs v0.1.9 -> v0.1.10 installed in place."
echo "PASS: Production-signed release upgrade completed without uninstalling the customer app."
echo "PASS: Both production APKs have the same certificate SHA-256: $prod_cert_v1"
echo "PASS: Production versionCodes increased from $prod_code_v1 to $prod_code_v2."

echo "Android installation lifecycle QA: all scenarios passed."
