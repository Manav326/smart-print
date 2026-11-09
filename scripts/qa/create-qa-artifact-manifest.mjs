#!/usr/bin/env node
import { createHash } from "node:crypto";
import { lstat, readdir, readFile, writeFile } from "node:fs/promises";
import { resolve, relative, sep } from "node:path";

const [rootArg, platformArg] = process.argv.slice(2);
const allowedPlatforms = new Set(["web", "webqa", "android", "windows"]);
if (!rootArg || !allowedPlatforms.has(platformArg)) {
  console.error("Usage: node scripts/qa/create-qa-artifact-manifest.mjs <artifact-directory> <web|webqa|android|windows>");
  process.exit(2);
}
const root = resolve(rootArg);
const sourceCommit = process.env.GITHUB_SHA ?? "";
const workflowRunId = process.env.GITHUB_RUN_ID ?? "";
const workflowRunNumber = process.env.GITHUB_RUN_NUMBER ?? "";
if (!/^[0-9a-f]{40}$/i.test(sourceCommit) || !/^\d+$/.test(workflowRunId) || !/^\d+$/.test(workflowRunNumber)) {
  throw new Error("GITHUB_SHA, GITHUB_RUN_ID and GITHUB_RUN_NUMBER are required to bind this artifact to its exact CI run.");
}

async function collect(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name))) {
    const absolute = resolve(directory, entry.name);
    const stat = await lstat(absolute);
    if (stat.isSymbolicLink()) throw new Error("Artifact directories must not contain symlinks: " + absolute);
    if (stat.isDirectory()) {
      files.push(...await collect(absolute));
      continue;
    }
    if (!stat.isFile()) throw new Error("Unsupported artifact entry: " + absolute);
    const path = relative(root, absolute).split(sep).join("/");
    if (path === "qa-artifact-manifest.json") continue;
    const bytes = await readFile(absolute);
    files.push({
      path,
      bytes: bytes.byteLength,
      sha256: createHash("sha256").update(bytes).digest("hex")
    });
  }
  return files;
}

const files = await collect(root);
if (files.length === 0) throw new Error("Refusing to create a manifest for an empty artifact directory.");
const manifest = {
  schemaVersion: 1,
  product: "SmartPrint",
  platform: platformArg,
  sourceCommit: sourceCommit.toLowerCase(),
  workflowRunId,
  workflowRunNumber,
  createdUtc: new Date().toISOString(),
  files
};
await writeFile(resolve(root, "qa-artifact-manifest.json"), JSON.stringify(manifest, null, 2) + "\n", "utf8");
console.log("Created exact-commit QA artifact manifest for " + platformArg + " (" + files.length + " files, run " + workflowRunNumber + ").");
