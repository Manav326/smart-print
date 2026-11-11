import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const portalRoot = fileURLToPath(new URL("..", import.meta.url));
const dist = join(portalRoot, "dist");
const pages = [
  ["index.html", "/"],
  ["print-shop-software/index.html", "/print-shop-software/"],
  ["qr-code-print-ordering/index.html", "/qr-code-print-ordering/"],
  ["print-job-management/index.html", "/print-job-management/"],
  ["windows-print-agent/index.html", "/windows-print-agent/"]
];
const htmlFiles = await Promise.all(pages.map(([path]) => readFile(join(dist, path), "utf8")));
const sitemap = await readFile(join(dist, "sitemap.xml"), "utf8");
const robots = await readFile(join(dist, "robots.txt"), "utf8");
const originMatch = htmlFiles[0].match(/<link rel="canonical" href="(https:\/\/[^/]+)\//);
assert.ok(originMatch, "homepage needs an absolute HTTPS canonical URL");
const origin = originMatch[1];

for (let index = 0; index < pages.length; index += 1) {
  const [path, route] = pages[index];
  const html = htmlFiles[index];
  assert.match(html, /<title>[^<]{10,}<\/title>/, `${path} needs a descriptive title`);
  assert.match(html, /<meta name="description" content="[^"]{50,}"/, `${path} needs a useful meta description`);
  assert.ok(html.includes(`<link rel="canonical" href="${origin}${route}"`), `${path} canonical must match its public route`);
  assert.ok(!html.includes("__SMARTPRINT_ORIGIN__"), `${path} has an unresolved origin placeholder`);
  assert.match(html, /<h1[ >]/, `${path} needs a primary heading`);
}
assert.ok(htmlFiles[0].includes('"@type":"SoftwareApplication"'), "homepage must describe the app with structured data");
assert.ok(htmlFiles[0].includes('"@type":"Organization"'), "homepage must describe its publisher");
for (const [, route] of pages) assert.ok(sitemap.includes(`<loc>${origin}${route}</loc>`), `sitemap must include ${route}`);
assert.doesNotMatch(sitemap, /\/(?:shop|admin|s|api|health)\//, "sitemap must exclude private application routes");
assert.ok(robots.includes(`Sitemap: ${origin}/sitemap.xml`), "robots.txt must reference the generated sitemap");
assert.match(robots, /Disallow: \/api\//);
assert.match(robots, /Disallow: \/downloads\//);
console.log("PASS: SmartPrint public SEO pages, canonical URLs, structured data, sitemap and robots.txt are valid.");
