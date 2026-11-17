import assert from "node:assert/strict";
import test from "node:test";
import { getPublicDownloadPath } from "../src/release-metadata.ts";

test("versioned downloads use the configured R2 custom domain when available", () => {
  assert.equal(
    getPublicDownloadPath("windows", "v1.2.3", "https://downloads.smartprint.example"),
    "https://downloads.smartprint.example/v1.2.3/SmartPrint-Windows-Setup.exe"
  );
  assert.equal(
    getPublicDownloadPath("android", "v1.2.3", "https://downloads.smartprint.example/"),
    "https://downloads.smartprint.example/v1.2.3/SmartPrint-Android.apk"
  );
});

test("versioned downloads retain the same-origin API path when R2 is not configured", () => {
  assert.equal(
    getPublicDownloadPath("windows", "v1.2.3", null),
    "/downloads/v1.2.3/SmartPrint-Windows-Setup.exe"
  );
  assert.equal(getPublicDownloadPath("android"), "/downloads/android");
});

test("version and artifact components are URL encoded", () => {
  assert.equal(
    getPublicDownloadPath("windows", "v1.2.3 test", "https://downloads.smartprint.example///"),
    "https://downloads.smartprint.example/v1.2.3%20test/SmartPrint-Windows-Setup.exe"
  );
});
