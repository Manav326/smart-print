import assert from 'node:assert/strict';
import { mkdirSync, writeFileSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { createInterface } from 'node:readline/promises';
import { stdin as input, stdout as output } from 'node:process';

const moduleDirectory = process.env.PLAYWRIGHT_MODULE_DIR;
if (!moduleDirectory) throw new Error('PLAYWRIGHT_MODULE_DIR is required.');
const qaConfig = JSON.parse(readFileSync(new URL('./smartprint-qa.config.json', import.meta.url), 'utf8'));
const configuredPublicOrigin = new URL(qaConfig.origins.public);
const baseUrl = new URL(process.env.SMARTPRINT_LIVE_QA_BASE_URL || configuredPublicOrigin.origin);
if (baseUrl.protocol !== 'https:' || baseUrl.origin !== configuredPublicOrigin.origin ||
    baseUrl.pathname !== '/' || baseUrl.search || baseUrl.hash || baseUrl.username || baseUrl.password) {
  throw new Error('This suite is restricted to the configured SmartPrint public origin: ' + configuredPublicOrigin.origin);
}
const origin = baseUrl.origin;
const outputDirectory = process.env.LIVE_QA_ARTIFACT_DIR || 'artifacts/live-environment-qa-windows-headed';
const screenshots = join(outputDirectory, 'screenshots');
mkdirSync(screenshots, { recursive: true });

const enabled = name => ['true', '1', 'yes'].includes((process.env[name] || '').toLowerCase());
const runShop = enabled('LIVE_QA_CHECK_SHOP_WORKSPACE');
const runAdmin = enabled('LIVE_QA_CHECK_PLATFORM_ADMIN');
const runStation = enabled('LIVE_QA_CHECK_CUSTOMER_STATION');
const runCustomerUploadWorkflow = enabled('LIVE_QA_CHECK_CUSTOMER_UPLOAD_WORKFLOW');
const expectedQaShopName = (process.env.SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME || '').trim();
const shopEmail = process.env.SMARTPRINT_LIVE_QA_SHOP_EMAIL || '';
const shopPassword = process.env.SMARTPRINT_LIVE_QA_SHOP_PASSWORD || '';
const adminEmail = process.env.SMARTPRINT_LIVE_QA_ADMIN_EMAIL || '';
const adminPassword = process.env.SMARTPRINT_LIVE_QA_ADMIN_PASSWORD || '';
const stationPath = (process.env.SMARTPRINT_LIVE_QA_STATION_PATH || '').trim();
if (runShop && (!shopEmail || !shopPassword)) throw new Error('Dedicated QA shop credentials are required.');
if (runAdmin && (!adminEmail || !adminPassword)) throw new Error('Dedicated QA admin credentials are required.');
if (runStation && !/^\/s\/[A-Za-z0-9_-]+\/?$/.test(stationPath)) throw new Error('A dedicated station path such as /s/QA_STATION is required.');
if (runCustomerUploadWorkflow && (!runShop || !runStation)) throw new Error('The real customer-upload workflow requires the dedicated shop account and dedicated QA station options.');
if (runCustomerUploadWorkflow && !expectedQaShopName) throw new Error('Set SMARTPRINT_LIVE_QA_EXPECTED_QA_SHOP_NAME to the exact dedicated QA shop display name.');

const { chromium } = await import(pathToFileURL(join(moduleDirectory, 'index.mjs')).href);
const rl = createInterface({ input, output });
const report = {
  target: origin,
  startedUtc: new Date().toISOString(),
  runner: `local Windows ${process.arch}`,
  browser: 'Microsoft Edge headed (Chromium fallback if Edge is unavailable)',
  safety: {
    ordersCreated: false,
    documentsUploaded: false,
    paymentAttempts: false,
    refundsRecorded: false,
    printDispatches: false,
    shopSettingsChanged: false,
    temporaryCustomerSession: runStation,
    printJobsCreated: false,
    testJobRejectedBeforePayment: false
  },
  checks: [],
  warnings: [],
  consoleErrors: [],
  serverErrors: [],
  resourceErrors: []
};

let browser;
let context;
let page;
let releaseMetadata = null;
let customerSession = null;
let signedInShopToken = null;

async function record(name, action) {
  const started = Date.now();
  try {
    const details = await action();
    const status = details?.status === 'SKIPPED' ? 'SKIPPED' : details?.warning ? 'WARN' : 'PASS';
    if (status === 'WARN') report.warnings.push({ name, message: details.warning });
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name, status, durationMs: Date.now() - started, details: details ?? null });
    console.log(`${status} ${name}`);
  } catch (error) {
    const message = String(error instanceof Error ? error.message : error).slice(0, 1200);
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name, status: 'FAIL', durationMs: Date.now() - started, error: message });
    console.error(`FAIL ${name}: ${message}`);
  }
}

async function maybePauseForCloudflare(response, path, label) {
  const originalStatus = response?.status?.() ?? 0;
  let pageText = await page.locator('body').innerText().catch(() => '');
  let title = await page.title().catch(() => '');
  let challenge = originalStatus === 403 || /just a moment|verify you are human|checking your browser|attention required/i.test(`${title}\n${pageText}`);
  if (!challenge) return response;

  // Give Cloudflare's normal browser challenge a chance to finish its own JS flow.
  // The harness does not spoof fingerprints, rotate identities, or bypass a challenge.
  await page.waitForTimeout(5000);
  pageText = await page.locator('body').innerText().catch(() => '');
  title = await page.title().catch(() => '');
  const appRendered = await page.locator('.sp-public, .auth-screen, .platform-admin-auth-screen, .customer-entry').count().catch(() => 0);
  const challengeStillVisible = /just a moment|verify you are human|checking your browser|attention required/i.test(`${title}\n${pageText}`);
  if (!challengeStillVisible && appRendered > 0) {
    return await page.goto(new URL(path, origin).toString(), { waitUntil: 'domcontentloaded', timeout: 60000 });
  }

  console.warn(`Cloudflare returned HTTP ${originalStatus || 'challenge'} while opening ${label}. Complete any challenge normally in the visible browser window, then return here.`);
  await rl.question('After the page is accessible in Edge, press Enter to retry this URL... ');
  return await page.goto(new URL(path, origin).toString(), { waitUntil: 'domcontentloaded', timeout: 60000 });
}

async function openPath(path, label = path) {
  let response = await page.goto(new URL(path, origin).toString(), { waitUntil: 'domcontentloaded', timeout: 45000 });
  response = await maybePauseForCloudflare(response, path, label);
  assert.ok(response, `${label} returned no HTTP response.`);
  assert.equal(response.status(), 200, `${label} returned HTTP ${response.status()}.`);
  assert.equal(new URL(response.url()).origin, origin, `${label} response left SmartPrint's canonical origin.`);
  assert.equal(new URL(page.url()).origin, origin, `${label} navigated away from SmartPrint's canonical origin.`);
  await page.waitForLoadState('networkidle', { timeout: 15000 }).catch(() => {});
  return response;
}

async function browserFetch(path, options = {}, readBody = true) {
  const requestUrl = new URL(path, origin).toString();
  assert.equal(new URL(requestUrl).origin, origin, 'Cross-origin test request blocked by harness.');
  const executeFetch = async () => page.evaluate(async ({ requestUrl, options, readBody }) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 30000);
    try {
      const response = await fetch(requestUrl, {
        cache: 'no-store',
        credentials: 'same-origin',
        signal: controller.signal,
        ...options
      });
      const headers = {};
      for (const [key, value] of response.headers.entries()) headers[key.toLowerCase()] = value;
      const body = readBody ? await response.text() : '';
      if (!readBody) await response.body?.cancel();
      return { status: response.status, url: response.url, body, headers };
    } finally {
      clearTimeout(timer);
    }
  }, { requestUrl, options, readBody });

  let result = await executeFetch();
  const contentType = (result.headers['content-type'] || '').toLowerCase();
  const looksLikeChallenge = /text\/html/.test(contentType) &&
    /cloudflare|just a moment|challenge-platform|verify you are human|attention required/i.test(result.body);
  if (result.status === 403 && looksLikeChallenge) {
    // A fetch response cannot run an interactive challenge. Navigate the same
    // same-origin path as a document, let the visible browser complete it, then retry.
    const challengeResponse = await page.goto(requestUrl, { waitUntil: 'domcontentloaded', timeout: 45000 });
    await maybePauseForCloudflare(challengeResponse, path, `API request ${path}`);
    result = await executeFetch();
  }
  assert.equal(new URL(result.url).origin, origin, `Request to ${path} redirected away from the SmartPrint origin.`);
  return result;
}

async function browserRangeProbe(path) {
  const requestUrl = new URL(path, origin).toString();
  const result = await page.evaluate(async requestUrl => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 30000);
    try {
      const response = await fetch(requestUrl, {
        cache: 'no-store',
        credentials: 'same-origin',
        headers: { Range: 'bytes=0-0' },
        signal: controller.signal
      });
      const headers = {};
      for (const [key, value] of response.headers.entries()) headers[key.toLowerCase()] = value;
      if (response.status !== 206) {
        // Never buffer an entire installer/APK if the origin ignores Range.
        await response.body?.cancel();
        return { status: response.status, url: response.url, headers, byteLength: null };
      }
      const reader = response.body?.getReader();
      const firstChunk = reader ? await reader.read() : { done: true, value: undefined };
      await reader?.cancel();
      return { status: response.status, url: response.url, headers, byteLength: firstChunk.value?.byteLength ?? 0 };
    } finally {
      clearTimeout(timer);
    }
  }, requestUrl);
  assert.equal(new URL(result.url).origin, origin, `${path} redirected outside the SmartPrint origin.`);
  assert.equal(result.status, 206, `${path} should return HTTP 206 for a one-byte range request.`);
  assert.match(result.headers['content-range'] || '', /^bytes 0-0\/\d+$/i, `${path} returned an invalid Content-Range.`);
  assert.equal(result.byteLength, 1, `${path} should transfer exactly one byte for the range probe.`);
  return { status: result.status, contentRange: result.headers['content-range'], byteLength: result.byteLength, contentType: result.headers['content-type'] || null };
}



async function browserFullDownloadProbe(path, expectedFileName) {
  const requestUrl = new URL(path, origin).toString();
  const result = await page.evaluate(async ({ requestUrl, expectedMaximumBytes }) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 120000);
    try {
      const response = await fetch(requestUrl, {
        cache: 'no-store',
        credentials: 'same-origin',
        headers: { Accept: 'application/octet-stream' },
        signal: controller.signal
      });
      const headers = {};
      for (const [key, value] of response.headers.entries()) headers[key.toLowerCase()] = value;
      if (!response.ok) {
        await response.body?.cancel();
        return { status: response.status, url: response.url, headers, bytesReceived: 0 };
      }

      const contentLength = Number(headers['content-length']);
      if (Number.isFinite(contentLength) && contentLength > expectedMaximumBytes) {
        await response.body?.cancel();
        return { status: response.status, url: response.url, headers, bytesReceived: 0, tooLarge: true };
      }

      // Read the complete asset but discard each chunk immediately. This
      // verifies a real browser transfer without retaining a 30–50 MB file
      // in memory or saving release binaries onto a shop machine.
      const reader = response.body?.getReader();
      if (!reader) return { status: response.status, url: response.url, headers, bytesReceived: 0 };
      let bytesReceived = 0;
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) break;
        bytesReceived += chunk.value?.byteLength ?? 0;
        if (bytesReceived > expectedMaximumBytes) {
          await reader.cancel();
          return { status: response.status, url: response.url, headers, bytesReceived, tooLarge: true };
        }
      }
      return { status: response.status, url: response.url, headers, bytesReceived };
    } finally {
      clearTimeout(timer);
    }
  }, { requestUrl, expectedMaximumBytes: qaConfig.downloads.maxTransferBytes });

  assert.equal(new URL(result.url).origin, origin, path + ' redirected outside the SmartPrint origin.');
  assert.equal(result.status, 200, path + ' should complete with HTTP 200.');
  assert.equal(result.tooLarge, undefined, path + ' exceeded the configured QA safety limit.');
  assert.ok(result.bytesReceived > 0, path + ' returned an empty download.');
  assert.ok(
    (result.headers['content-disposition'] || '').toLowerCase().includes(expectedFileName.toLowerCase()),
    path + ' did not advertise the expected attachment name ' + expectedFileName + '.'
  );

  const contentLength = Number(result.headers['content-length']);
  if (Number.isFinite(contentLength) && contentLength > 0) {
    assert.equal(result.bytesReceived, contentLength, path + ' did not transfer its advertised Content-Length.');
  }
  return {
    status: result.status,
    fileName: expectedFileName,
    bytesReceived: result.bytesReceived,
    contentLength: Number.isFinite(contentLength) && contentLength > 0 ? contentLength : null,
    contentType: result.headers['content-type'] || null,
    contentDisposition: result.headers['content-disposition'] || null
  };
}


function createSyntheticPdfBase64() {
  const stream = 'BT /F1 18 Tf 72 720 Td (SmartPrint QA synthetic test document) Tj ET';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    '<< /Length ' + Buffer.byteLength(stream, 'ascii') + ' >>\nstream\n' + stream + '\nendstream'
  ];
  let pdf = '%PDF-1.4\n';
  const offsets = [];
  for (let index = 0; index < objects.length; index += 1) {
    offsets.push(Buffer.byteLength(pdf, 'ascii'));
    pdf += (index + 1) + ' 0 obj\n' + objects[index] + '\nendobj\n';
  }
  const xrefOffset = Buffer.byteLength(pdf, 'ascii');
  pdf += 'xref\n0 ' + (objects.length + 1) + '\n';
  pdf += '0000000000 65535 f \n';
  for (const offset of offsets) pdf += String(offset).padStart(10, '0') + ' 00000 n \n';
  pdf += 'trailer\n<< /Size ' + (objects.length + 1) + ' /Root 1 0 R >>\n';
  pdf += 'startxref\n' + xrefOffset + '\n%%EOF\n';
  return Buffer.from(pdf, 'ascii').toString('base64');
}

async function uploadSyntheticPdf(sessionToken, batchId, idempotencyKey, fileName, base64) {
  await page.bringToFront();
  const result = await page.evaluate(async ({ sessionToken, batchId, idempotencyKey, fileName, base64 }) => {
    const bytes = Uint8Array.from(atob(base64), character => character.charCodeAt(0));
    const form = new FormData();
    form.append('file', new Blob([bytes], { type: 'application/pdf' }), fileName);
    form.append('colourMode', 'Mono');
    form.append('paperSize', 'A4');
    form.append('isDuplex', 'false');
    form.append('copies', '1');
    form.append('orientation', 'AUTO');
    form.append('batchName', 'SmartPrint automated QA');
    const response = await fetch('/api/v1/public/sessions/current/jobs', {
      method: 'POST',
      credentials: 'same-origin',
      cache: 'no-store',
      signal: AbortSignal.timeout(30000),
      headers: {
        'X-Print-Session': sessionToken,
        'Idempotency-Key': idempotencyKey,
        'X-Print-Batch-Id': batchId
      },
      body: form
    });
    return { status: response.status, body: await response.text() };
  }, { sessionToken, batchId, idempotencyKey, fileName, base64 });
  let parsed = null;
  try { parsed = JSON.parse(result.body); } catch {}
  return { ...result, parsed };
}

async function runRealCustomerUploadWorkflow() {
  assert.ok(customerSession?.sessionToken, 'No live customer session token was captured from the dedicated station.');
  assert.ok(signedInShopToken?.accessToken, 'No authenticated QA shop token is available.');
  assert.equal(customerSession.shopName, expectedQaShopName,
    'The selected station belongs to a different shop. No document was uploaded.');

  const sessionHeaders = { 'X-Print-Session': customerSession.sessionToken };
  const invalid = await uploadSyntheticPdf(
    customerSession.sessionToken,
    crypto.randomUUID(),
    crypto.randomUUID(),
    'smartprint-invalid-document.pdf',
    Buffer.from('This is not a PDF document.', 'ascii').toString('base64')
  );
  assert.equal(invalid.status, 422,
    'A malformed PDF should be rejected by the real document validator; received HTTP ' + invalid.status + '.');

  const batchId = crypto.randomUUID();
  const idempotencyKey = crypto.randomUUID();
  const fileName = 'smartprint-live-qa-' + new Date().toISOString().replace(/[:.]/g, '-') + '.pdf';
  const pdfBase64 = createSyntheticPdfBase64();
  let job = null;

  try {
    const upload = await uploadSyntheticPdf(
      customerSession.sessionToken, batchId, idempotencyKey, fileName, pdfBase64
    );
    assert.ok([200, 201].includes(upload.status),
      'Real customer upload failed with HTTP ' + upload.status + ': ' + upload.body.slice(0, 600));
    assert.ok(upload.parsed?.job?.id, 'Upload response did not contain a real PrintJob ID.');
    job = upload.parsed.job;
    report.safety.documentsUploaded = true;
    report.safety.printJobsCreated = true;

    assert.equal(job.pageCount, 1, 'Synthetic test PDF should be analysed as exactly one page.');
    assert.equal(job.status, 'AWAITING_CONFIGURATION', 'New uploaded document should enter configuration.');

    const replay = await uploadSyntheticPdf(
      customerSession.sessionToken, batchId, idempotencyKey, fileName, pdfBase64
    );
    assert.equal(replay.status, 200, 'Idempotent replay should return the existing upload.');
    assert.equal(replay.parsed?.job?.id, job.id, 'Idempotent replay created or returned a different PrintJob.');
    assert.equal(replay.parsed?.duplicateRequest, true, 'Idempotent replay was not marked duplicate.');

    const quote = await browserFetch('/api/v1/public/jobs/' + job.id + '/quote', {
      method: 'POST',
      headers: sessionHeaders,
      body: JSON.stringify({
        colourMode: 'Mono', paperSize: 'A4', isDuplex: false, copies: 1, orientation: 'AUTO'
      })
    });
    assert.equal(quote.status, 200, 'Real quote calculation returned HTTP ' + quote.status + ': ' + quote.body.slice(0, 500));
    const quoteBody = JSON.parse(quote.body);
    assert.equal(quoteBody.id, job.id);
    assert.equal(quoteBody.currency, 'INR');
    assert.ok(Number.isInteger(quoteBody.quotedAmountMinor) && quoteBody.quotedAmountMinor >= 0);
    assert.ok(Date.parse(quoteBody.quoteExpiresUtc) > Date.now(), 'Quote expiry must be in the future.');

    const accept = await browserFetch('/api/v1/public/jobs/' + job.id + '/accept-quote', {
      method: 'POST', headers: sessionHeaders
    });
    assert.equal(accept.status, 200, 'Customer quote acceptance returned HTTP ' + accept.status + ': ' + accept.body.slice(0, 500));
    const accepted = JSON.parse(accept.body);
    assert.equal(accepted.id, job.id);
    assert.equal(accepted.status, 'AWAITING_SHOP_APPROVAL');

    const shopHeaders = {
      Authorization: (signedInShopToken.tokenType || 'Bearer') + ' ' + signedInShopToken.accessToken
    };
    const queue = await browserFetch(
      '/api/v1/shop/print-jobs?offset=0&take=100&search=' + encodeURIComponent(job.jobNumber),
      { headers: shopHeaders }
    );
    assert.equal(queue.status, 200, 'Shop queue could not read the just-created real job.');
    const jobs = JSON.parse(queue.body);
    assert.ok(Array.isArray(jobs) && jobs.some(item => item.id === job.id),
      'The real job was not visible in the correct shop queue.');

    const rejection = await browserFetch('/api/v1/shop/print-jobs/' + job.id + '/reject', {
      method: 'POST', headers: shopHeaders
    });
    assert.equal(rejection.status, 200, 'QA cleanup rejection returned HTTP ' + rejection.status + ': ' + rejection.body.slice(0, 500));
    assert.equal(JSON.parse(rejection.body).status, 'REJECTED');
    report.safety.testJobRejectedBeforePayment = true;

    const status = await browserFetch('/api/v1/public/jobs/' + job.id + '/status', {
      headers: sessionHeaders
    });
    assert.equal(status.status, 200, 'Customer could not retrieve the final job state.');
    const statusBody = JSON.parse(status.body);
    assert.equal(statusBody.job?.status, 'REJECTED', 'Customer status did not reflect the shop-side QA rejection.');
    return {
      stationShop: customerSession.shopName,
      jobNumber: job.jobNumber,
      pageCount: job.pageCount,
      quotedAmountMinor: quoteBody.quotedAmountMinor,
      currency: quoteBody.currency,
      idempotentReplay: 'same PrintJob returned; no duplicate created',
      finalStatus: statusBody.job.status,
      rejectedBeforePayment: true,
      paymentAttempted: false,
      printDispatched: false
    };
  } catch (error) {
    // Best-effort cleanup only for states the API explicitly permits rejecting.
    if (job?.id && signedInShopToken?.accessToken && !report.safety.testJobRejectedBeforePayment) {
      try {
        const shopHeaders = {
          Authorization: (signedInShopToken.tokenType || 'Bearer') + ' ' + signedInShopToken.accessToken
        };
        const status = await browserFetch('/api/v1/public/jobs/' + job.id + '/status', { headers: sessionHeaders });
        const current = status.status === 200 ? (JSON.parse(status.body).job?.status || '') : '';
        if (['AWAITING_SHOP_APPROVAL', 'AWAITING_PAYMENT'].includes(current)) {
          await browserFetch('/api/v1/shop/print-jobs/' + job.id + '/reject', {
            method: 'POST', headers: shopHeaders
          });
          report.safety.testJobRejectedBeforePayment = true;
        } else {
          report.warnings.push({ name: 'QA upload cleanup', message: 'Test job ' + job.jobNumber + ' remains in state ' + (current || 'unknown') + '; manual QA-shop review is required.' });
        }
      } catch (cleanupError) {
        report.warnings.push({ name: 'QA upload cleanup', message: 'Could not reject test job ' + job.jobNumber + ': ' + String(cleanupError).slice(0, 500) });
      }
    }
    throw error;
  }
}

async function screenshot(name) {
  await page.screenshot({ path: join(screenshots, `${name}.png`), fullPage: true, animations: 'disabled' }).catch(() => {});
}


async function assertLiveResponsiveLayout(label, viewport) {
  const result = await page.evaluate(() => {
    const width = window.innerWidth;
    const root = document.documentElement;
    const body = document.body;
    const controls = Array.from(document.querySelectorAll('button,a,input,select,textarea,[role="button"]'));
    const offenders = [];
    for (const element of controls) {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      if (style.display === 'none' || style.visibility === 'hidden' || rect.width <= 0 || rect.height <= 0) continue;
      const sidebar = element.closest('.sidebar');
      if (sidebar && !sidebar.classList.contains('sidebar-open') && width <= 850) continue;
      let parent = element.parentElement;
      let insideScroller = false;
      while (parent && parent !== body) {
        const parentStyle = getComputedStyle(parent);
        const parentRect = parent.getBoundingClientRect();
        if (['auto', 'scroll'].includes(parentStyle.overflowX) &&
            parent.scrollWidth > parent.clientWidth + 1 &&
            parentRect.left >= -1 && parentRect.right <= width + 1) {
          insideScroller = true;
          break;
        }
        parent = parent.parentElement;
      }
      if (!insideScroller && (rect.left < -1 || rect.right > width + 1)) {
        offenders.push({
          tag: element.tagName.toLowerCase(),
          label: element.getAttribute('aria-label') || element.textContent?.trim().slice(0, 60) || '',
          left: Math.round(rect.left),
          right: Math.round(rect.right)
        });
      }
    }
    return {
      viewportWidth: width,
      rootScrollWidth: root.scrollWidth,
      bodyScrollWidth: body.scrollWidth,
      theme: root.dataset.theme || 'unset',
      offenders: offenders.slice(0, 12)
    };
  });
  assert.ok(result.rootScrollWidth <= viewport.width + 1,
    label + ' root horizontally overflows at ' + viewport.width + 'px: ' + JSON.stringify(result));
  assert.ok(result.bodyScrollWidth <= viewport.width + 1,
    label + ' body horizontally overflows at ' + viewport.width + 'px: ' + JSON.stringify(result));
  assert.equal(result.offenders.length, 0,
    label + ' has visible interactive controls clipped at ' + viewport.width + 'px: ' + JSON.stringify(result.offenders));
  return result;
}

async function ensureLiveTheme(theme) {
  const current = await page.locator('html').getAttribute('data-theme');
  if (current === theme) return;
  const accessibleName = theme === 'dark' ? 'Switch to dark theme' : 'Switch to light theme';
  await page.getByRole('button', { name: accessibleName }).first().click();
  await page.waitForFunction(expected => document.documentElement.dataset.theme === expected, theme, { timeout: 10000 });
}

async function runPublicLiveResponsiveMatrix() {
  const routes = [
    { path: '/', selector: '.sp-public', label: 'public landing' },
    { path: '/shop', selector: '.auth-screen', label: 'shop sign-in' },
    { path: '/login', selector: '.auth-screen', label: 'legacy login' },
    { path: '/signup', selector: '.auth-screen', label: 'shop registration' },
    { path: '/admin', selector: '.platform-admin-auth-screen', label: 'platform-admin sign-in' }
  ];
  const viewports = [
    { width: 1440, height: 1000 },
    { width: 1024, height: 900 },
    { width: 768, height: 1024 },
    { width: 520, height: 900 },
    { width: 390, height: 844 }
  ];
  const results = [];
  for (const route of routes) {
    await openPath(route.path, route.label);
    await page.locator(route.selector).first().waitFor({ state: 'visible', timeout: 15000 });
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      await page.waitForTimeout(120);
      results.push(await assertLiveResponsiveLayout(route.label, viewport));
    }
    await screenshot('responsive-' + route.label.replace(/[^a-z0-9-]/gi, '-'));
  }
  return { realPagesChecked: routes.length, viewportChecks: results.length, mockedApiRoutes: 0 };
}

async function runAuthenticatedLiveResponsiveMatrix(label, sections) {
  const viewports = [
    { width: 1440, height: 1000 },
    { width: 1024, height: 900 },
    { width: 768, height: 1024 },
    { width: 520, height: 900 },
    { width: 390, height: 844 }
  ];
  const results = [];
  for (const theme of ['light', 'dark']) {
    await ensureLiveTheme(theme);
    for (const viewport of viewports) {
      await page.setViewportSize(viewport);
      for (const section of sections) {
        if (viewport.width <= 850) {
          const openNavigation = page.getByRole('button', { name: 'Open navigation' });
          if (await openNavigation.count()) await openNavigation.first().click();
        }
        const item = page.locator('.sidebar .nav-item').filter({ hasText: section }).first();
        await item.waitFor({ state: 'visible', timeout: 12000 });
        await item.click();
        await page.waitForFunction(
          name => [...document.querySelectorAll('.sidebar .nav-item')]
            .some(element => element.textContent.trim() === name && element.classList.contains('active')),
          section,
          { timeout: 12000 }
        );
        await page.waitForTimeout(100);
        results.push({
          section,
          theme,
          ...(await assertLiveResponsiveLayout(label + ' / ' + section + ' / ' + theme, viewport))
        });
      }
    }
  }
  return { workspace: label, sections: sections.length, themes: 2, viewports: viewports.length, realApi: true, layoutChecks: results.length };
}

async function visitRoute(path, selector, name) {
  await openPath(path, name);
  await page.locator(selector).first().waitFor({ state: 'visible', timeout: 20000 });
  const insecureResources = await page.evaluate(() => performance.getEntriesByType('resource').map(item => item.name).filter(url => url.startsWith('http://')));
  assert.equal(insecureResources.length, 0, `${name} loaded cleartext HTTP resources: ${insecureResources.join(', ')}`);
  await screenshot(name.replace(/[^a-z0-9-]/gi, '-').toLowerCase());
  return { url: page.url(), title: await page.title() };
}

async function signInAndVisit(label, email, password, sections, formSelector, buttonName) {
  await openPath(label === 'shop' ? '/shop' : '/admin', `${label} sign-in`);
  await page.locator(formSelector).waitFor({ state: 'visible', timeout: 15000 });
  await page.getByLabel('Email address').fill(email);
  await page.locator('input[type="password"]').first().fill(password);
  await page.getByRole('button', { name: buttonName }).click();
  try {
    await page.locator('.app-shell').waitFor({ state: 'visible', timeout: 30000 });
  } catch {
    const authError = (await page.locator('.auth-error').allTextContents().catch(() => [])).join(' ');
    throw new Error(`${label} account did not reach the workspace. ${authError || 'Check the dedicated QA account or API response.'}`);
  }
  await page.locator('.sidebar .nav-item').first().waitFor({ state: 'visible', timeout: 15000 });

  if (label === 'shop') {
    const raw = await page.evaluate(() => sessionStorage.getItem('qrtoprint.auth.shop'));
    try { signedInShopToken = raw ? JSON.parse(raw) : null; } catch { signedInShopToken = null; }
  }

  // Verify the actual scoped bearer token can read its own scope and cannot cross roles.
  const scope = label === 'shop' ? 'shop' : 'admin';
  const storedTokens = await page.evaluate(scope => {
    const raw = sessionStorage.getItem(`qrtoprint.auth.${scope}`);
    if (!raw) return null;
    try { return JSON.parse(raw); } catch { return null; }
  }, scope);
  assert.ok(storedTokens?.accessToken, `${label} login did not store its access token in the expected isolated session.`);
  const authHeaders = { Authorization: `${storedTokens.tokenType || 'Bearer'} ${storedTokens.accessToken}` };
  if (label === 'shop') {
    const ownScope = await browserFetch('/api/v1/shop/profile', { headers: authHeaders });
    assert.equal(ownScope.status, 200, 'Authenticated shop account could not read its own shop profile.');
    const crossScope = await browserFetch('/api/v1/platform/overview', { headers: authHeaders });
    assert.ok([401, 403].includes(crossScope.status), `Shop account unexpectedly accessed platform overview (HTTP ${crossScope.status}).`);
  } else {
    const ownScope = await browserFetch('/api/v1/platform/overview', { headers: authHeaders });
    assert.equal(ownScope.status, 200, 'Authenticated platform admin could not read platform overview.');
    const crossScope = await browserFetch('/api/v1/shop/profile', { headers: authHeaders });
    assert.ok([401, 403].includes(crossScope.status), `Platform admin unexpectedly accessed shop profile (HTTP ${crossScope.status}).`);
  }

  const visited = [];
  for (const section of sections) {
    const item = page.locator('.sidebar .nav-item').filter({ hasText: section }).first();
    await item.waitFor({ state: 'visible', timeout: 12000 });
    await item.click();
    await page.waitForFunction(
      label => [...document.querySelectorAll('.sidebar .nav-item')].some(element => element.textContent.trim() === label && element.classList.contains('active')),
      section,
      { timeout: 12000 }
    );
    visited.push(section);
  }
  const responsive = await runAuthenticatedLiveResponsiveMatrix(label, sections);
  await screenshot(`${label}-workspace`);
  return { sectionsVisited: visited, identityScopeChecks: 'passed', responsive, readOnly: true };
}

try {
  try {
    browser = await chromium.launch({ channel: 'msedge', headless: false });
    report.browser = 'Microsoft Edge (headed)';
  } catch {
    console.warn('Microsoft Edge channel was unavailable; using the installed Playwright Chromium in headed mode.');
    browser = await chromium.launch({ headless: false });
    report.browser = 'Playwright Chromium (headed)';
  }
  report.browserVersion = browser.version();
  context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: false });
  page = await context.newPage();
  page.setDefaultTimeout(15000);
  page.on('pageerror', error => report.consoleErrors.push(String(error.message).slice(0, 500)));
  page.on('console', message => {
    if (message.type() !== 'error') return;
    const location = message.location()?.url || '';
    if (!location || location.startsWith(origin)) report.consoleErrors.push(String(message.text()).slice(0, 500));
  });
  page.on('response', response => {
    try {
      const url = new URL(response.url());
      const resourceType = response.request().resourceType();
      if (url.origin === origin && response.status() >= 500) report.serverErrors.push({ status: response.status(), path: url.pathname });
      if (url.origin === origin && ['script', 'stylesheet', 'font'].includes(resourceType) && response.status() >= 400) {
        report.resourceErrors.push({ status: response.status(), path: url.pathname, resourceType });
      }
    } catch {}
  });

  await record('Public landing page loads in local headed browser', async () => {
    const result = await visitRoute('/', '.sp-public', 'public landing');
    const heading = (await page.locator('.sp-hero h1').innerText()).replace(/\s+/g, ' ').trim();
    assert.match(heading, /From their phone/i, 'Landing hero heading did not match the expected SmartPrint content.');
    return { ...result, heading };
  });

  await record('API liveness', async () => {
    const response = await browserFetch('/health/live');
    assert.equal(response.status, 200);
    const body = JSON.parse(response.body);
    assert.equal(body.status, 'live');
    assert.equal(body.service, 'qrtoprint-api');
    return { status: response.status, service: body.service, version: body.version };
  });

  await record('API readiness and PostgreSQL connectivity', async () => {
    const response = await browserFetch('/health/ready');
    assert.equal(response.status, 200);
    const body = JSON.parse(response.body);
    assert.equal(body.status, 'ready');
    assert.equal(body.database, 'connected');
    return { status: response.status, database: body.database };
  });

  await record('Unauthenticated platform API is denied', async () => {
    const response = await browserFetch('/api/v1/platform/overview');
    assert.ok([401, 403].includes(response.status), `Expected 401/403, received ${response.status}.`);
    return { status: response.status };
  });

  await record('Unknown API route returns 404', async () => {
    const response = await browserFetch('/api/v1/live-qa-route-that-does-not-exist');
    assert.equal(response.status, 404);
    return { status: response.status };
  });

  await record('Public release metadata and cache policy', async () => {
    const response = await browserFetch(qaConfig.downloads.statusPath);
    assert.ok([200, 503].includes(response.status), `Unexpected release metadata HTTP ${response.status}.`);
    if (response.status === 503) return { status: response.status, warning: 'Upstream release metadata unavailable; retryable state is expected.' };
    const body = JSON.parse(response.body);
    assert.ok(/^v?\d+\.\d+\.\d+/.test(body.version || ''), `Release version is invalid: ${body.version}`);
    assert.equal(typeof body.windowsAvailable, 'boolean');
    assert.equal(typeof body.androidAvailable, 'boolean');
    assert.match((response.headers['cache-control'] || '').toLowerCase(), /no-store/, 'Release metadata must use Cache-Control: no-store.');
    releaseMetadata = body;
    return { status: response.status, cacheControl: response.headers['cache-control'] || null, ...body };
  });

  await record('Windows download supports an exact one-byte range response', async () => {
    if (!releaseMetadata || releaseMetadata.windowsAvailable !== true) return { status: 'SKIPPED', reason: 'Windows installer is not confirmed available by release metadata.' };
    return await browserRangeProbe(qaConfig.downloads.windowsPath);
  });

  await record('Windows installer completes a full same-origin browser download', async () => {
    if (!releaseMetadata || releaseMetadata.windowsAvailable !== true) return { status: 'SKIPPED', reason: 'Windows installer is not confirmed available by release metadata.' };
    return await browserFullDownloadProbe(qaConfig.downloads.windowsPath, qaConfig.downloads.windowsAssetName);
  });

  await record('Android download supports an exact one-byte range response when published', async () => {
    if (!releaseMetadata || releaseMetadata.androidAvailable !== true) return { status: 'SKIPPED', reason: 'Android asset is not confirmed available by release metadata.' };
    return await browserRangeProbe(qaConfig.downloads.androidPath);
  });

  await record('Android APK completes a full same-origin browser download', async () => {
    if (!releaseMetadata || releaseMetadata.androidAvailable !== true) return { status: 'SKIPPED', reason: 'Android asset is not confirmed available by release metadata.' };
    return await browserFullDownloadProbe(qaConfig.downloads.androidPath, qaConfig.downloads.androidAssetName);
  });

  await record('Shop sign-in route renders', async () => visitRoute('/shop', '.auth-screen', 'shop login'));
  await record('Legacy login route renders', async () => visitRoute('/login', '.auth-screen', 'legacy login'));
  await record('Shop registration route renders without submitting', async () => {
    const result = await visitRoute('/signup', '.auth-screen', 'shop signup');
    for (const label of ['Shop or business name', 'Your name', 'Email address']) await page.getByLabel(label).waitFor({ state: 'visible' });
    await page.locator('input[type="password"]').first().waitFor({ state: 'visible' });
    return { ...result, registrationSubmitted: false };
  });
  await record('Live public/auth pages responsive across 5 viewport sizes', async () => runPublicLiveResponsiveMatrix());

  await record('Platform-admin sign-in route renders', async () => {
    const result = await visitRoute('/admin', '.platform-admin-auth-screen', 'platform admin login');
    await page.getByLabel('Email address').waitFor({ state: 'visible' });
    await page.locator('input[type="password"]').first().waitFor({ state: 'visible' });
    return result;
  });

  if (runShop) {
    await record('Authenticated shop workspace and all navigation sections', async () => signInAndVisit(
      'shop', shopEmail, shopPassword,
      ['Order Journey', 'Overview', 'Print Queue', 'Printers', 'QR Stations', 'Print Agent', 'Pricing', 'Subscription', 'Payments', 'Customers', 'Reports', 'Settings'],
      '.auth-screen', 'Sign in to workspace'
    ));
  } else {
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name: 'Authenticated shop workspace', status: 'SKIPPED', reason: 'Run with -RunShopWorkspace and use dedicated QA credentials.' });
  }

  if (runAdmin) {
    await record('Authenticated platform-admin workspace and all navigation sections', async () => signInAndVisit(
      'admin', adminEmail, adminPassword,
      ['Overview', 'Shop tenants', 'Plans & entitlements', 'Shop subscriptions', 'Operations & retention'],
      '.platform-admin-auth-screen', 'Sign in to administration'
    ));
  } else {
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name: 'Authenticated platform-admin workspace', status: 'SKIPPED', reason: 'Run with -RunPlatformAdmin and use dedicated QA credentials.' });
  }

  if (runStation) {
    await record('Dedicated customer QR station creates a real temporary session', async () => {
      const result = await visitRoute(stationPath, '.customer-entry', 'customer QR station');
      const state = page.locator('.customer-entry-card .entry-eyebrow');
      await state.waitFor({ state: 'visible', timeout: 25000 });
      const stateLabel = (await state.innerText()).trim();
      assert.equal(stateLabel, 'SECURE SESSION CREATED');
      const stationParts = stationPath.split('/').filter(Boolean);
      const publicCode = stationParts[1];
      assert.equal(stationParts[0], 's');
      assert.ok(publicCode, 'Dedicated station code could not be parsed.');
      const sessionToken = await page.evaluate(code => sessionStorage.getItem('qrtoprint.customer-session:' + code), publicCode);
      assert.ok(sessionToken, 'Customer session token was not saved for the dedicated station.');
      const current = await browserFetch('/api/v1/public/sessions/current', {
        headers: { 'X-Print-Session': sessionToken }
      });
      assert.equal(current.status, 200, 'The real customer session token was not accepted by the API.');
      const currentBody = JSON.parse(current.body);
      customerSession = {
        sessionToken,
        shopName: currentBody.shop?.displayName || '',
        expiresUtc: currentBody.expiresUtc,
        stationPath,
        localAgentAvailable: Boolean(currentBody.localAgent)
      };
      if (expectedQaShopName) assert.equal(customerSession.shopName, expectedQaShopName,
        'The dedicated station does not belong to the expected QA shop.');
      return {
        ...result,
        stationPath: '[configured dedicated QA station]',
        sessionCreated: true,
        sessionApiVerified: true,
        shopName: customerSession.shopName,
        expiresUtc: customerSession.expiresUtc,
        localAgentAvailable: customerSession.localAgentAvailable
      };
    });
  } else {
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name: 'Dedicated customer QR station', status: 'SKIPPED', reason: 'Run with -RunCustomerStation and a disposable enabled test station.' });
  }

  if (runCustomerUploadWorkflow) {
    await record('Real PDF upload, analysis, idempotency, quote, customer acceptance, shop visibility and safe rejection', async () => runRealCustomerUploadWorkflow());
  } else {
    report.checks.push({ id: `SP-WEB-${String(report.checks.length + 1).padStart(3, '0')}`, name: 'Real customer document and quote workflow', status: 'SKIPPED', reason: 'Enable the opt-in customer-upload workflow with a dedicated QA shop and station; the default run creates no jobs.' });
  }

  await record('HTTPS origin continuity for application and health routes', async () => {
    const paths = ['/', '/shop', '/login', '/signup', '/admin', '/health/live', '/health/ready'];
    const results = [];
    for (const path of paths) {
      await openPath(path, path);
      results.push({ path, status: 200, finalOrigin: new URL(page.url()).origin });
      assert.equal(new URL(page.url()).origin, origin);
    }
    return { routesChecked: results.length, results };
  });

  await record('No same-origin server 5xx, broken JS/CSS/fonts or uncaught browser exceptions', async () => {
    assert.equal(report.serverErrors.length, 0, JSON.stringify(report.serverErrors));
    assert.equal(report.resourceErrors.length, 0, JSON.stringify(report.resourceErrors));
    assert.equal(report.consoleErrors.length, 0, report.consoleErrors.join('\n'));
    return { serverErrors: 0, resourceErrors: 0, consoleErrors: 0 };
  });
} finally {
  report.finishedUtc = new Date().toISOString();
  report.summary = {
    passed: report.checks.filter(check => check.status === 'PASS').length,
    failed: report.checks.filter(check => check.status === 'FAIL').length,
    skipped: report.checks.filter(check => check.status === 'SKIPPED').length,
    warnings: report.checks.filter(check => check.status === 'WARN').length
  };
  writeFileSync(join(outputDirectory, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  console.log('\nSmartPrint headed live QA summary:');
  console.log(JSON.stringify(report.summary, null, 2));
  console.log(`Evidence: ${outputDirectory}`);
  await context?.close().catch(() => {});
  await browser?.close().catch(() => {});
  rl.close();
}

if (report.summary.failed > 0) process.exitCode = 1;