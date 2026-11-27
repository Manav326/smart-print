import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { Script } from "node:vm";
import test from "node:test";

const page = readFileSync(new URL("../../apps/portal/public/account-deletion/index.html", import.meta.url), "utf8");
const policy = readFileSync(new URL("../../apps/portal/public/privacy-policy/index.html", import.meta.url), "utf8");
const script = page.match(/<script>([\s\S]*?)<\/script>/i)?.[1];

test("account deletion page script parses as JavaScript", () => {
  assert.ok(script, "Expected the account-deletion page script");
  assert.doesNotThrow(() => new Script(script));
});
test("deletion flow authenticates an existing customer and checks their profile", () => {
  assert.match(page, /\/api\/v1\/auth\/customer-login/);
  assert.match(page, /\/api\/v1\/auth\/google\/customer-existing/);
  assert.match(page, /\/api\/v1\/customer\/me/);
  assert.match(page, /\/api\/v1\/customer\/account/);
  assert.match(page, /Authorization/);
  assert.match(page, /Type DELETE to confirm/);
});
test("bearer and refresh tokens are not persisted in browser storage", () => {
  assert.doesNotMatch(page, /localStorage|sessionStorage|indexedDB/i);
  assert.match(page, /let accessToken = null/);
  assert.match(page, /let refreshToken = null/);
});
test("privacy policy describes app, print documents, ads and deletion", () => {
  assert.match(policy, /SmartPrint by QuantaEdge/);
  assert.match(policy, /Android application/);
  assert.match(policy, /Private Printing/);
  assert.match(policy, /72-hour/);
  assert.match(policy, /account deletion/i);
});
