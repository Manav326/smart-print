import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const configuration = JSON.parse(
  readFileSync(new URL("../../desktop/src-tauri/tauri.conf.json", import.meta.url), "utf8")
);
const csp = configuration.app?.security?.csp ?? "";
const directives = new Map(
  csp.split(";").map(value => {
    const [name, ...sources] = value.trim().split(/\s+/);
    return [name, sources];
  })
);

test("Windows desktop permits local PDF blob URLs inside the document preview iframe", () => {
  const frameSources = directives.get("frame-src") ?? directives.get("default-src") ?? [];
  assert.ok(
    frameSources.includes("blob:"),
    "PDF previews are fetched as Blob URLs and rendered in an iframe; frame-src must explicitly allow blob:."
  );
});

test("Windows desktop keeps document preview frames local and does not allow arbitrary remote frames", () => {
  const frameSources = directives.get("frame-src") ?? [];
  assert.ok(frameSources.includes("'self'"));
  assert.ok(frameSources.includes("blob:"));
  assert.ok(frameSources.includes("asset:"));
  assert.ok(frameSources.includes("http://asset.localhost"));
  assert.ok(frameSources.includes("http://tauri.localhost"));
  assert.equal(frameSources.includes("https:"), false);
  assert.equal(frameSources.includes("http:"), false);
});
