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
  ["windows-print-agent/index.html", "/windows-print-agent/"],
  ["smartprint-faq/index.html", "/smartprint-faq/"]
];
const htmlFiles = await Promise.all(pages.map(([path]) => readFile(join(dist, path), "utf8")));
const sitemap = await readFile(join(dist, "sitemap.xml"), "utf8");
const robots = await readFile(join(dist, "robots.txt"), "utf8");
const nginx = await readFile(join(portalRoot, "nginx.conf"), "utf8");
const originMatch = htmlFiles[0].match(/<link rel="canonical" href="(https:\/\/[^/]+)\//);
assert.ok(originMatch, "homepage needs an absolute HTTPS canonical URL");
const origin = originMatch[1];
const expectedOriginValue = process.env.SMARTPRINT_PUBLIC_BASE_URL
  || process.env.PUBLIC_BASE_URL
  || process.env.VITE_PUBLIC_BASE_URL;
if (expectedOriginValue) {
  const expectedOrigin = new URL(expectedOriginValue).origin;
  assert.equal(origin, expectedOrigin, "generated canonicals must match the configured production origin");
}

for (let index = 0; index < pages.length; index += 1) {
  const [path, route] = pages[index];
  const html = htmlFiles[index];
  assert.match(html, /<title>[^<]{10,}<\/title>/, `${path} needs a descriptive title`);
  assert.match(html, /<meta name="description" content="[^"]{50,}"/, `${path} needs a useful meta description`);
  assert.ok(html.includes(`<link rel="canonical" href="${origin}${route}"`), `${path} canonical must match its public route`);
  assert.ok(!html.includes("__SMARTPRINT_ORIGIN__"), `${path} has an unresolved origin placeholder`);
  assert.match(html, /<h1[ >]/, `${path} needs a primary heading`);
  assert.match(html, /<meta name="robots" content="index,follow,max-image-preview:large">/, `${path} must be indexable`);
  assert.match(html, /<meta property="og:title"/, `${path} needs a social title`);
  assert.match(html, /<meta property="og:description"/, `${path} needs a social description`);
  const jsonLdScripts = [...html.matchAll(/<script type="application\\/ld\\+json">([\\s\\S]*?)<\\/script>/g)];
  assert.ok(jsonLdScripts.length > 0, `${path} needs structured data`);
  for (const match of jsonLdScripts) assert.doesNotThrow(() => JSON.parse(match[1]), `${path} has invalid JSON-LD`);
}
assert.ok(htmlFiles[0].includes('"@type":"SoftwareApplication"'), "homepage must describe the app with structured data");
assert.ok(htmlFiles[0].includes('"@type":"Organization"'), "homepage must describe its publisher");
for (const [, route] of pages) assert.ok(sitemap.includes(`<loc>${origin}${route}</loc>`), `sitemap must include ${route}`);
assert.doesNotMatch(sitemap, /\/(?:shop|admin|s|api|health)\//, "sitemap must exclude private application routes");
assert.ok(robots.includes(`Sitemap: ${origin}/sitemap.xml`), "robots.txt must reference the generated sitemap");
assert.doesNotMatch(robots, /__SMARTPRINT_ORIGIN__/, "robots.txt must not contain unresolved origin placeholders");

// App-only routes must be explicitly excluded from indexing at the HTTP layer,
// including the trailing-slash variants of the authentication entry points.
for (const route of ["/admin", "/shop", "/login", "/login/", "/signup", "/signup/"]) {
  const escaped = route.replace(/[.*+?^${}()|[\\]\\]/g, "\\assert.ok(robots.includes(`Sitemap: ${origin}/sitemap.xml`), "robots.txt must reference the generated sitemap");

const groupFor");
  const routePattern = new RegExp(`location = ${escaped.replace("/", "\\/")} \\\\{[\\s\\S]*?add_header X-Robots-Tag "noindex, nofollow" always;`);
  assert.match(nginx, routePattern, `${route} must return a noindex response header`);
}
for (const route of ["/admin/", "/shop/", "/s/"]) {
  assert.ok(nginx.includes(`location ^~ ${route}`), `${route} must have a private SPA route handler`);
  assert.match(nginx, /location @noindex_spa \\\{[\\s\\S]*?add_header X-Robots-Tag "noindex, nofollow" always;/, "private SPA fallback must keep its noindex response header");
}

const groupFor = (agent) => {
  const marker = `User-agent: ${agent}\n`;
  const start = robots.indexOf(marker);
  assert.notEqual(start, -1, `robots.txt must include ${agent}`);
  const bodyStart = start + marker.length;
  const nextGroup = robots.indexOf("\n\nUser-agent: ", bodyStart);
  return robots.slice(bodyStart, nextGroup === -1 ? robots.length : nextGroup);
};
const protectedPaths = ["/api/", "/health/", "/downloads/"];
const explicitlyPermittedAgents = [
  "OAI-SearchBot", "PerplexityBot", "Claude-SearchBot", "Claude-User",
  "GPTBot", "ClaudeBot", "Google-Extended"
];
for (const agent of explicitlyPermittedAgents) {
  const group = groupFor(agent);
  assert.match(group, /^Allow: \/$/m, `${agent} must be allowed on public paths`);
  assert.doesNotMatch(group, /^Disallow: \/$/m, `${agent} must not be blocked site-wide`);
  for (const path of protectedPaths) assert.ok(group.includes(`Disallow: ${path}`), `${agent} must not access ${path}`);
}
const trainingPreference = groupFor("__usage_ai-training");
assert.match(trainingPreference, /^Allow: \/$/m, "AI-training preference must permit public content");
for (const path of protectedPaths) assert.ok(trainingPreference.includes(`Disallow: ${path}`), `AI-training preference must exclude ${path}`);
const defaultGroup = groupFor("*");
assert.match(defaultGroup, /^Allow: \/$/m, "other standards-compliant crawlers must be allowed by default");
for (const path of protectedPaths) assert.ok(defaultGroup.includes(`Disallow: ${path}`), `default crawler policy must exclude ${path}`);
console.log("PASS: SmartPrint public SEO pages, canonical URLs, structured data, sitemap and search/training crawler permissions are valid.");
