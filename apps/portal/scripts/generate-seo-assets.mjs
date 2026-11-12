import { readdir, readFile, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const portalRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
const distRoot = join(portalRoot, "dist");
const fallbackOrigin = "https://smartprint.quantaedge.in";
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
const publicPaths = ["/", "/features/", "/whatsapp-printing-alternative/", "/print-shop-software/", "/qr-code-print-ordering/", "/qr-printing-for-xerox-shops/", "/print-job-management/", "/document-privacy-private-printing/", "/photo-id-printing/", "/windows-print-agent/", "/smartprint-faq/"];
await replaceOriginPlaceholder(distRoot, origin);

const sitemapEntries = publicPaths.map(path => `  <url><loc>${origin}${path}</loc></url>`).join("\n");
await writeFile(join(distRoot, "sitemap.xml"), `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${sitemapEntries}\n</urlset>\n`, "utf8");

const protectedPaths = "Disallow: /api/\nDisallow: /health/\nDisallow: /downloads/";
const searchAgents = ["OAI-SearchBot", "ChatGPT-User", "PerplexityBot", "Perplexity-User", "Claude-SearchBot", "Claude-User"];
// Search and user-directed answer retrieval are permitted independently of model training.
const searchAgentRules = searchAgents
  .map(agent => `User-agent: ${agent}\nAllow: /\n${protectedPaths}`)
  .join("\n\n");

// Model-training access is not required for search visibility. Keep it blocked unless
// the site owner explicitly chooses to permit it. Google-Extended additionally
// controls Gemini Apps / Vertex AI grounding, not just training.
const modelTrainingAgents = ["GPTBot", "ClaudeBot"];
const trainingAgentRules = modelTrainingAgents
  .map(agent => `User-agent: ${agent}\nDisallow: /`)
  .join("\n\n");

// Google-Extended is explicitly allowed for Gemini Apps / Vertex AI grounding,
// with the site owner's consent to Google's associated Gemini model-training use.
const googleExtendedRules = `User-agent: Google-Extended\\nAllow: /\\n${protectedPaths}`;

// Draft IETF AIPREF token: express the site owner's preference against model training.
const trainingPreference = "User-agent: __usage_ai-training\nDisallow: /";
const defaultRules = `User-agent: *\nAllow: /\n${protectedPaths}`;
const robots = `${searchAgentRules}\n\n${trainingAgentRules}\n\n${googleExtendedRules}\n\n${trainingPreference}\n\n${defaultRules}\n\nSitemap: ${origin}/sitemap.xml\n`;
await writeFile(join(distRoot, "robots.txt"), robots, "utf8");
console.log(`Generated SmartPrint sitemap and robots.txt for ${origin}.`);
