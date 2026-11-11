import { readdir, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const portalRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
const distRoot = join(portalRoot, "dist");
const fallbackOrigin = "https://smartprint.quantardge.in";
const configuredOrigin = process.env.VITE_PUBLIC_BASE_URL
  || process.env.SMARTPRINT_PUBLIC_BASE_URL
  || process.env.PUBLIC_BASE_URL
  || fallbackOrigin;

function canonicalOrigin(value) {
  let parsed;
  try { parsed = new URL(value); }
  catch { throw new Error("VITE_PUBLIC_BASE_URL must be a valid absolute HTTPS origin."); }
  if (parsed.protocol !== "https:" || !parsed.hostname || parsed.username || parsed.password
    || parsed.pathname !== "/" || parsed.search || parsed.hash) {
    throw new Error("VITE_PUBLIC_BASE_URL must be a canonical HTTPS origin without credentials, path, query or fragment.");
  }
  return parsed.origin;
}

async function replaceOriginPlaceholder(directory, origin) {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) await replaceOriginPlaceholder(path, origin);
    else if (entry.isFile() && entry.name.endsWith(".html")) {
      const source = await readFile(path, "utf8");
      await writeFile(path, source.replaceAll("__SMARTPRINT_ORIGIN__", origin), "utf8");
    }
  }
}

const origin = canonicalOrigin(configuredOrigin);
const publicPaths = ["/", "/print-shop-software/", "/qr-code-print-ordering/", "/print-job-management/", "/windows-print-agent/"];
await replaceOriginPlaceholder(distRoot, origin);

const sitemapEntries = publicPaths.map(path => `  <url><loc>${origin}${path}</loc></url>`).join("\n");
await writeFile(join(distRoot, "sitemap.xml"), `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${sitemapEntries}\n</urlset>\n`, "utf8");
await writeFile(join(distRoot, "robots.txt"), `User-agent: *\nAllow: /\nDisallow: /api/\nDisallow: /health/\nDisallow: /downloads/\n\nSitemap: ${origin}/sitemap.xml\n`, "utf8");
console.log(`Generated SmartPrint sitemap and robots.txt for ${origin}.`);
