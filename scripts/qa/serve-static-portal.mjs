#!/usr/bin/env node
import { createReadStream } from "node:fs";
import { stat } from "node:fs/promises";
import { createServer } from "node:http";
import { extname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const [rootArg, portArg = "4174"] = process.argv.slice(2);
if (!rootArg || !/^\d{1,5}$/.test(portArg)) {
  console.error("Usage: node scripts/qa/serve-static-portal.mjs <dist-directory> [port]");
  process.exit(2);
}
const root = resolve(rootArg);
const port = Number(portArg);
if (port < 1024 || port > 65535) throw new Error("Static QA port must be between 1024 and 65535.");
const mime = new Map([
  [".html", "text/html; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".mjs", "text/javascript; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".svg", "image/svg+xml"],
  [".png", "image/png"],
  [".jpg", "image/jpeg"],
  [".jpeg", "image/jpeg"],
  [".webp", "image/webp"],
  [".ico", "image/x-icon"],
  [".woff", "font/woff"],
  [".woff2", "font/woff2"],
  [".ttf", "font/ttf"],
  [".pdf", "application/pdf"]
]);

const server = createServer(async (request, response) => {
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.writeHead(405, { Allow: "GET, HEAD" }).end();
    return;
  }
  let pathname;
  try {
    pathname = decodeURIComponent(new URL(request.url ?? "/", "http://127.0.0.1").pathname);
  } catch {
    response.writeHead(400).end("Bad request");
    return;
  }
  const requested = pathname === "/" ? "index.html" : pathname.replace(/^\/+/, "");
  let target = resolve(root, requested);
  const relative = target.slice(root.length).replace(/^[/\\]+/, "");
  if (target !== root && !relative && target !== resolve(root, "index.html")) {
    response.writeHead(403).end("Forbidden");
    return;
  }
  if (target !== root && !target.startsWith(root + sep)) {
    response.writeHead(403).end("Forbidden");
    return;
  }
  try {
    const info = await stat(target);
    if (info.isDirectory()) target = resolve(target, "index.html");
    await stat(target);
  } catch {
    target = resolve(root, "index.html");
    try { await stat(target); }
    catch { response.writeHead(404).end("Not found"); return; }
  }
  response.writeHead(200, {
    "Content-Type": mime.get(extname(target).toLowerCase()) ?? "application/octet-stream",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff"
  });
  if (request.method === "HEAD") { response.end(); return; }
  createReadStream(target).on("error", () => {
    if (!response.headersSent) response.writeHead(500);
    response.end("Read error");
  }).pipe(response);
});

server.listen(port, "127.0.0.1", () => {
  console.log("SmartPrint pulled portal artifact serving at http://127.0.0.1:" + port);
});
for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
