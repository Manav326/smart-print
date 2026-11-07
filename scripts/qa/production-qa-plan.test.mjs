import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const plan = readFileSync(new URL("../../docs/PRODUCTION_QA_TEST_PLAN.md", import.meta.url), "utf8");
const caseIds = [...plan.matchAll(/^\| ([A-Z]+-\d{3}) \|/gm)].map(match => match[1]);

test("production QA catalogue contains at least 300 numbered cases", () => {
  assert.ok(caseIds.length >= 300, `Expected at least 300 catalogue cases; found ${caseIds.length}.`);
});

test("production QA catalogue has no duplicate case IDs", () => {
  const duplicates = caseIds.filter((id, index) => caseIds.indexOf(id) !== index);
  assert.deepEqual([...new Set(duplicates)], [], "Every QA case ID must be unique.");
});

test("production QA catalogue covers every product and quality domain", () => {
  const groups = new Set(caseIds.map(id => id.split("-")[0]));
  for (const group of [
    "RLS", "AUTH", "SHOP", "AGT", "QR", "AND", "DOC", "QTE", "JOB",
    "REF", "PAY", "CUS", "RPT", "ADM", "SEC", "REC", "OPS", "UX"
  ]) {
    assert.ok(groups.has(group), `Missing test group: ${group}`);
  }
});

test("production QA plan retains all mandatory end-to-end journeys", () => {
  for (let index = 1; index <= 8; index += 1) {
    const id = `E2E-${String(index).padStart(2, "0")}`;
    assert.ok(plan.includes(`### ${id}`), `Missing mandatory journey: ${id}`);
  }
});

test("production QA plan retains all documented blocker closure gates", () => {
  for (let index = 1; index <= 12; index += 1) {
    const id = `BLK-${String(index).padStart(2, "0")}`;
    assert.ok(plan.includes(`| ${id} |`), `Missing blocker closure gate: ${id}`);
  }
});

test("production QA plan defines live terminal progress and safe continuation rules", () => {
  assert.match(plan, /Live terminal progress contract/);
  assert.match(plan, /\[QA-STEP-START\]/);
  assert.match(plan, /\[QA-CASE-START\]/);
  assert.match(plan, /Continue executing independent test records/);
  assert.match(plan, /Final status: NOT SIGNED OFF/);
});


test("local Windows runner executes the isolated Photo Studio browser matrix in full and E2E-only modes", () => {
  const runner = readFileSync(new URL("./run-smartprint-qa.ps1", import.meta.url), "utf8");
  assert.match(runner, /function Invoke-PhotoStudioBrowserAcceptance/);
  assert.equal((runner.match(/Invoke-PhotoStudioBrowserAcceptance -Node \$node/g) ?? []).length, 2,
    "Both the full local run and incremental E2E-only run must execute the Photo Studio browser matrix.");
  assert.match(runner, /apps\/portal\/tests\/theme-responsive\.browser\.mjs/);
  assert.match(runner, /http:\/\/127\.0\.0\.1:4173/);
  assert.match(runner, /http:\/\/127\.0\.0\.1:1420/);
  assert.match(runner, /RESPONSIVE_ARTIFACT_DIR/);
  assert.match(runner, /SMARTPRINT_PNPM_COMMAND/);
  assert.match(runner, /SMARTPRINT_PNPM_PREFIX/);
  assert.match(runner, /--frozen-lockfile/);
  assert.match(runner, /--prefer-offline/);
  assert.match(runner, /packageManager/);
    assert.match(runner, /already in use\. The existing process was left untouched/);
  const workspaceManifest = JSON.parse(readFileSync(new URL("../../package.json", import.meta.url), "utf8"));
  assert.equal(workspaceManifest.packageManager, "pnpm@10.18.0",
    "The visual suite must resolve the exact package-manager version pinned by the workspace.");
});
