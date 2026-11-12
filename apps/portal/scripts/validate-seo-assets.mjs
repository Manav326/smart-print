import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const portalRoot = fileURLToPath(new URL("..", import.meta.url));
const dist = join(portalRoot, "dist");
const pages = [
  ["index.html", "/"],
  ["features/index.html", "/features/"],
  ["whatsapp-printing-alternative/index.html", "/whatsapp-printing-alternative/"],
  ["print-shop-software/index.html", "/print-shop-software/"],
  ["qr-code-print-ordering/index.html", "/qr-code-print-ordering/"],
  ["qr-printing-for-xerox-shops/index.html", "/qr-printing-for-xerox-shops/"],
  ["print-job-management/index.html", "/print-job-management/"],
  ["document-privacy-private-printing/index.html", "/document-privacy-private-printing/"],
  ["photo-id-printing/index.html", "/photo-id-printing/"],
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
  || process.env.VITE_PUBLIC_BASE_URL
  || "https://smartprint.quantaedge.in";
const expectedUrl = new URL(expectedOriginValue);
assert.equal(expectedUrl.protocol, "https:", "configured public origin must use HTTPS");
assert.equal(expectedUrl.pathname, "/", "configured public origin must not include a path");
assert.equal(expectedUrl.search, "", "configured public origin must not include a query");
assert.equal(expectedUrl.hash, "", "configured public origin must not include a fragment");
assert.equal(origin, expectedUrl.origin, "generated canonicals must match the configured production origin");

for (let index = 0; index < pages.length; index += 1) {
  const [path, route] = pages[index];
  const html = htmlFiles[index];
  const titleMatch = html.match(/<title>([^<]+)<\/title>/);
  const descriptionMatch = html.match(/<meta name="description" content="([^"]+)"/);
  assert.ok(titleMatch && titleMatch[1].length >= 10, `${path} needs a descriptive title`);
  assert.ok(titleMatch[1].length <= 60, `${path} title must be 60 characters or fewer`);
  assert.ok(descriptionMatch && descriptionMatch[1].length >= 50, `${path} needs a useful meta description`);
  assert.ok(descriptionMatch[1].length <= 160, `${path} meta description must be 160 characters or fewer`);
  assert.ok(html.includes(`<link rel="canonical" href="${origin}${route}"`), `${path} canonical must match its public route`);
  assert.ok(!html.includes("__SMARTPRINT_ORIGIN__"), `${path} has an unresolved origin placeholder`);
  assert.match(html, /<h1[ >]/, `${path} needs a primary heading`);
  assert.match(html, /<meta name="robots" content="index,follow,max-image-preview:large">/, `${path} must be indexable`);
  assert.match(html, /<meta property="og:title"/, `${path} needs a social title`);
  assert.match(html, /<meta property="og:description"/, `${path} needs a social description`);
  const jsonLdScripts = [...html.matchAll(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/g)];
  assert.ok(jsonLdScripts.length > 0, `${path} needs structured data`);
  for (const match of jsonLdScripts) {
    assert.doesNotThrow(() => JSON.parse(match[1]), `${path} has invalid JSON-LD`);
  }
}
assert.ok(htmlFiles[0].includes('"@type":"SoftwareApplication"'), "homepage must describe the app with structured data");
assert.ok(htmlFiles[0].includes('"@type":"Organization"'), "homepage must describe its publisher");
const faqHtml = htmlFiles[pages.findIndex(([, route]) => route === "/smartprint-faq/")];
const faqSchemas = [...faqHtml.matchAll(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/g)]
  .map(match => JSON.parse(match[1]));
assert.ok(faqSchemas.some(schema => (schema["@graph"] ?? [schema]).some(node => node["@type"] === "FAQPage" && node.mainEntity?.length === 16)),
  "the existing FAQ page must expose its 16 visible questions as FAQPage structured data");
const sitemapLocations = [...sitemap.matchAll(/<loc>([^<]+)<\/loc>/g)].map(match => match[1]);
assert.deepEqual(sitemapLocations, pages.map(([, route]) => `${origin}${route}`), "sitemap must contain exactly the intended public pages");
assert.doesNotMatch(sitemap, /\/(?:shop|admin|s|api|health|downloads)\//, "sitemap must exclude private application and download routes");
assert.ok(robots.includes(`Sitemap: ${origin}/sitemap.xml`), "robots.txt must reference the generated sitemap");
assert.doesNotMatch(robots, /__SMARTPRINT_ORIGIN__/, "robots.txt must not contain unresolved origin placeholders");

// App-only routes must be explicitly excluded from indexing at the HTTP layer,
// including trailing-slash variants of the authentication entry points.
for (const route of ["/admin", "/shop", "/login", "/login/", "/signup", "/signup/"]) {
  const escapedRoute = route.replace(/\//g, "\\/");
  const match = nginx.match(new RegExp("location = " + escapedRoute + " \\{([^}]*)\\}"));
  assert.ok(match, `${route} needs an exact Nginx location`);
  assert.match(match[1], /add_header X-Robots-Tag "noindex, nofollow" always;/, `${route} must return a noindex response header`);
}
for (const route of ["/admin/", "/shop/", "/s/"]) {
  assert.ok(nginx.includes(`location ^~ ${route}`), `${route} must have a private SPA route handler`);
}
const privateFallback = nginx.match(/location @noindex_spa\s*\{([\s\S]*?)\n    \}/);
assert.ok(privateFallback, "private SPA routes need a named fallback");
assert.match(privateFallback[1], /add_header X-Robots-Tag "noindex, nofollow" always;/, "private SPA fallback must keep its noindex response header");

const groupFor = (agent) => {
  const marker = `User-agent: ${agent}\n`;
  const start = robots.indexOf(marker);
  assert.notEqual(start, -1, `robots.txt must include ${agent}`);
  const bodyStart = start + marker.length;
  const nextGroup = robots.indexOf("\n\nUser-agent: ", bodyStart);
  return robots.slice(bodyStart, nextGroup === -1 ? robots.length : nextGroup);
};
const protectedPaths = ["/api/", "/health/", "/downloads/"];
const searchAgents = ["OAI-SearchBot", "ChatGPT-User", "PerplexityBot", "Perplexity-User", "Claude-SearchBot", "Claude-User"];
for (const agent of searchAgents) {
  const group = groupFor(agent);
  assert.match(group, /^Allow: \/$/m, `${agent} must be allowed on public paths`);
  assert.doesNotMatch(group, /^Disallow: \/$/m, `${agent} must not be blocked site-wide`);
  for (const path of protectedPaths) assert.ok(group.includes(`Disallow: ${path}`), `${agent} must not access ${path}`);
}
const modelTrainingAgents = ["GPTBot", "ClaudeBot"];
for (const agent of modelTrainingAgents) {
  const group = groupFor(agent);
  assert.match(group, /^Disallow: \/$/m, `${agent} must remain blocked until the site owner explicitly permits model training / Gemini grounding`);
}
const googleExtendedGroup = groupFor("Google-Extended");
assert.match(googleExtendedGroup, /^Allow: \/$/m, "Google-Extended must be explicitly allowed for Gemini grounding by site-owner choice");
for (const path of protectedPaths) assert.ok(googleExtendedGroup.includes(`Disallow: ${path}`), `Google-Extended must not access ${path}`);
const trainingPreference = groupFor("__usage_ai-training");
assert.match(trainingPreference, /^Disallow: \/$/m, "the shared AI-training preference must opt out of training");
const defaultGroup = groupFor("*");
assert.match(defaultGroup, /^Allow: \/$/m, "other standards-compliant crawlers must be allowed by default");
for (const path of protectedPaths) assert.ok(defaultGroup.includes(`Disallow: ${path}`), `default crawler policy must exclude ${path}`);
console.log("PASS: SmartPrint public SEO pages, canonicals, JSON-LD, sitemap, private-route indexing and AI crawler permissions are valid.");
