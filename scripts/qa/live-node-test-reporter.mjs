import path from "node:path";

function displayLocation(data) {
  if (!data.file) return "";
  const relativePath = path.relative(process.cwd(), data.file) || path.basename(data.file);
  const suffix = data.line ? `:${data.line}` : "";
  return ` — ${relativePath.replaceAll(path.sep, "/")}${suffix}`;
}

function cleanMessage(value) {
  return String(value ?? "").replace(/[\r\n]+/g, " ").trim();
}

export default async function* liveNodeTestReporter(source) {
  let started = 0;
  let completed = 0;

  for await (const event of source) {
    const data = event.data ?? {};

    if (event.type === "test:dequeue") {
      started += 1;
      const indentation = "  ".repeat(Math.min(Number(data.nesting) || 0, 8));
      yield `[TEST ${String(started).padStart(3, "0")} START] ${indentation}${cleanMessage(data.name)}${displayLocation(data)}\n`;
      continue;
    }

    if (event.type === "test:complete") {
      completed += 1;
      const details = data.details ?? {};
      const status = data.skip ? "SKIP" : data.todo ? "TODO" : details.passed ? "PASS" : "FAIL";
      const duration = Number.isFinite(details.duration_ms) ? ` (${Math.round(details.duration_ms)} ms)` : "";
      const indentation = "  ".repeat(Math.min(Number(data.nesting) || 0, 8));
      const suite = details.type === "suite";
      const label = suite ? "TEST SUITE" : "TEST";
      yield `[${label} ${status}] ${indentation}${cleanMessage(data.name)}${displayLocation(data)}${duration}\n`;

      if (status === "FAIL") {
        const error = details.error?.cause?.message ?? details.error?.message;
        if (error) yield `  [TEST ERROR] ${cleanMessage(error)}\n`;
      }
      continue;
    }

    if (event.type === "test:summary") {
      const counts = data.counts ?? {};
      const file = data.file ? ` file=${path.relative(process.cwd(), data.file).replaceAll(path.sep, "/")}` : "";
      const duration = Number.isFinite(data.duration_ms) ? ` duration=${Math.round(data.duration_ms)}ms` : "";
      yield `[TEST RUN SUMMARY]${file} tests=${counts.tests ?? 0} passed=${counts.passed ?? 0} failed=${counts.failed ?? 0} skipped=${counts.skipped ?? 0} todo=${counts.todo ?? 0}${duration} (started events=${started}, completed events=${completed})\n`;
      continue;
    }

    if (event.type === "test:stdout" || event.type === "test:stderr") {
      const message = String(data.message ?? "").replace(/[\r\n]+$/g, "");
      if (message) {
        const prefix = event.type === "test:stderr" ? "TEST STDERR" : "TEST OUTPUT";
        for (const line of message.split(/\r?\n/)) yield `  [${prefix}] ${line}\n`;
      }
      continue;
    }

    if (event.type === "test:diagnostic") {
      const message = cleanMessage(data.message);
      if (message) yield `  [TEST DIAGNOSTIC] ${message}\n`;
    }
  }
}
