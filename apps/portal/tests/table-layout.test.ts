import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { verifiedPdfExportPath } from "../src/print-output.ts";

const styles = readFileSync(new URL("../src/styles.css", import.meta.url), "utf8");
const app = readFileSync(new URL("../src/App.tsx", import.meta.url), "utf8");

test("generic portal tables allow long values to wrap inside their own cells", () => {
  const globalTableRule = styles.match(/(?:^|})table\{([^}]*)\}/)?.[1] ?? "";
  assert.notEqual(globalTableRule, "", "the shared table rule must remain present");
  assert.doesNotMatch(globalTableRule, /white-space\s*:\s*nowrap/i);
  assert.match(styles, /\.table-scroll\{[^}]*overflow-x:auto/s);
  assert.match(styles, /table th,\s*table td\s*\{[^}]*overflow-wrap:\s*anywhere/s);
});

test("print queue columns stay constrained and scroll inside their own viewport", () => {
  assert.match(app, /className="table-scroll queue-table-scroll"><table className="queue-jobs-table">/);
  assert.match(app, /const singleSavedPdfPath = batch\.jobs\.length === 1 \? verifiedPdfExportPath\(first\) : null;/);
  assert.match(app, /className="queue-pdf-path"/);
  assert.match(styles, /\.queue-jobs-table\s*\{[^}]*min-width:\s*1100px[^}]*table-layout:\s*fixed/s);
  assert.match(styles, /\.queue-table-scroll\s*\{[^}]*overflow-x:\s*auto/s);
  assert.match(styles, /\.queue-jobs-table \.queue-actions\s*\{[^}]*flex-wrap:\s*wrap/s);
});

test("confirmed PDF export locations are shown as a separate, wrapping detail", () => {
  assert.match(app, /verifiedPdfExportPath\(job\) && <div className="queue-batch-file-output"/);
  assert.match(app, /<span>\{t\("Saved PDF location"\)\}<\/span>/);
  assert.match(styles, /\.queue-batch-file-output code\s*\{[^}]*overflow-wrap:\s*anywhere/s);
});

test("PDF output path is only returned for a completed PDF export receipt", () => {
  const path = String.raw`C:\Users\Public\Documents\QRtoPrint Exports\SP-261031-63C63EF2_IMG-20261031-WA0000.pdf`;
  assert.equal(verifiedPdfExportPath({
    outputType: "PDF_EXPORT",
    outputCompletedUtc: "2026-10-31T15:35:27Z",
    outputFilePath: path
  }), path);
  assert.equal(verifiedPdfExportPath({
    outputType: "PHYSICAL",
    outputCompletedUtc: "2026-10-31T15:35:27Z",
    outputFilePath: path
  }), null);
  assert.equal(verifiedPdfExportPath({
    outputType: "PDF_EXPORT",
    outputCompletedUtc: null,
    outputFilePath: path
  }), null);
  assert.equal(verifiedPdfExportPath({
    outputType: "PDF_EXPORT",
    outputCompletedUtc: "2026-10-31T15:35:27Z",
    outputFilePath: "   "
  }), null);
});

test("platform tables retain a scroll container and intentional minimum widths", () => {
  assert.match(styles, /\.platform-table-scroll\s*\{[^}]*overflow-x:\s*auto/s);
  assert.match(styles, /\.platform-tenant-table\{[^}]*min-width:830px/s);
  assert.match(styles, /\.platform-subs-copy small\s*\{[^}]*white-space:\s*normal/s);
  assert.match(styles, /\.queue-jobs-table \.status-badge\s*\{[^}]*white-space:\s*normal/s);
});
