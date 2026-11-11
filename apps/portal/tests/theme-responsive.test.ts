import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { readDarkModePreference } from "../src/ui-preferences.ts";
import { getLocalPreviewEndpointCandidates, isPrivateLocalPreviewEndpoint } from "../src/local-preview-routing.ts";

const read = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");


test("folder and per-file Copies inputs capture values before scheduling React state updates", () => {
  const app = read("../src/App.tsx");
  const copyLabels: string[] = [];
  let cursor = 0;
  while (true) {
    const start = app.indexOf('<label className="customer-folder-copies">', cursor);
    if (start < 0) break;
    const end = app.indexOf("</label>", start);
    assert.ok(end > start, "Each Copies label must be properly closed.");
    copyLabels.push(app.slice(start, end));
    cursor = end + "</label>".length;
  }
  assert.equal(copyLabels.length, 2, "The shared flow must have folder-default and per-file Copies inputs.");

  for (const [index, copyLabel] of copyLabels.entries()) {
    const setter = index === 0 ? "setGroupSettings(current=>" : "setFileSettings(current=>";
    const blurStart = copyLabel.indexOf("onBlur={event=>{");
    assert.ok(blurStart >= 0, "Each Copies input must keep a blur handler.");
    const changeHandler = copyLabel.slice(copyLabel.indexOf("onChange={event=>{"), blurStart);
    const blurHandler = copyLabel.slice(blurStart);

    for (const [name, handler] of [["change", changeHandler], ["blur", blurHandler]] as const) {
      const captureIndex = handler.indexOf("event.currentTarget.value");
      const updateIndex = handler.indexOf(setter);
      assert.ok(captureIndex >= 0 && updateIndex > captureIndex,
        `The ${name} handler must read currentTarget before scheduling the state update.`);
      assert.equal(handler.indexOf("event.currentTarget", updateIndex), -1,
        `The ${name} handler must not read currentTarget inside a state updater.`);
    }
  }
});

test("platform operational checkbox captures its event value before scheduling state", () => {
  const source = read("../src/PlatformOperationsSection.tsx");
  assert.ok(source.includes("const checked = event.target.checked;"));
  assert.ok(source.includes("allowPaidJobClaimsWhenSuspended: checked"));
  assert.ok(!source.includes("allowPaidJobClaimsWhenSuspended: event.target.checked"));
});

test("first web visit is light and saved selection is restored before rendering", () => {
  const html = read("../index.html");
  const desktopHtml = read("../../desktop/index.html");
  assert.equal(readDarkModePreference(), false);
  for (const source of [html, desktopHtml]) {
    assert.match(source, /localStorage\.getItem\("qrtoprint\.theme"\)/);
    assert.match(source, /\? "dark" : "light"/);
    assert.match(source, /document\.documentElement\.dataset\.theme = theme/);
  }
});

test("one accessible theme control is available to all application route families", () => {
  const toggle = read("../src/ThemeToggleButton.tsx");
  const app = read("../src/App.tsx");
  const publicPage = read("../src/PublicLandingPage.tsx");
  const shopAuth = read("../src/AuthScreen.tsx");
  const adminAuth = read("../src/PlatformAdminAuthScreen.tsx");
  const admin = read("../src/PlatformAdminConsole.tsx");

  assert.match(toggle, /aria-label=\{dark \? "Switch to light theme" : "Switch to dark theme"\}/);
  assert.match(app, /<PublicLandingPage dark=\{dark\} onToggleTheme=\{toggleTheme\}/);
  assert.match(app, /<CustomerEntry publicCode=\{route\.publicCode\} dark=\{dark\}/);
  assert.match(app, /<ShopConsole tokens=\{tokens\} onSignOut=\{onShopSignOut\} dark=\{dark\}/);
  assert.match(app, /<PlatformAdminConsole tokens=\{tokens\} onSignOut=\{onAdminSignOut\} dark=\{dark\}/);
  assert.match(publicPage, /ThemeToggleButton/);
  assert.match(shopAuth, /ThemeToggleButton/);
  assert.match(adminAuth, /ThemeToggleButton/);
  assert.match(admin, /ThemeToggleButton/);
});

test("light and dark palettes cover authentication, customer QR and public landing surfaces", () => {
  const css = read("../src/styles.css");
  const landing = read("../src/public-landing.css");

  assert.match(css, /html\[data-theme="light"\] \.auth-screen/);
  assert.match(css, /html\[data-theme="dark"\] \.customer-entry/);
  assert.match(css, /\.theme-toggle-button/);
  assert.match(landing, /html\[data-theme="dark"\] \.sp-public/);
  assert.match(css, /html\[data-theme="dark"\] \.customer-entry:not\(\.smartprint-native\) \.entry-local-agent\s*\{[^}]*background:\s*#182335[^}]*border-color:\s*#344158/s,
    "The browser customer local-agent card must switch to a dark surface with readable copy.");
  assert.match(landing, /html\[data-theme="dark"\] \.sp-public \.sp-download-card h3\s*\{[^}]*color:\s*#edf1fa/s,
    "Download card titles must not retain dark foregrounds on dark card surfaces.");
  assert.match(css, /html\[data-theme="dark"\] \.auth-input input::placeholder\s*\{[^}]*color:\s*#aab6ca/s,
    "Dark authentication placeholders must remain readable on dark input surfaces.");
  assert.match(css, /html\[data-theme="dark"\] \.auth-card-footer,[\s\S]*?color:\s*#aab6ca/s,
    "Dark authentication helper text must not use the legacy low-contrast footer colour.");
  const photoStudio = read("../src/PhotoStudio.module.css");
  assert.match(photoStudio, /\.panelHeading\s*>\s*div\s*\{[^}]*display:grid[^}]*\}/s,
    "Photo Studio title and helper copy must render on separate lines.");
});

test("web shell keeps navigation, header and workspace independently usable at desktop and mobile sizes", () => {
  const css = read("../src/styles.css");
  assert.match(css, /@media \(min-width: 851px\)/);
  assert.match(css, /@media \(max-width: 850px\)/);
  assert.match(css, /\.app-shell > \.sidebar > \.primary-nav\s*\{[^}]*overflow-y:\s*auto/s);
  assert.match(css, /\.app-shell > \.main-panel > \.topbar\s*\{[^}]*position:\s*sticky/s);
  assert.match(css, /\.app-shell > \.main-panel > \.content-wrap\s*\{[^}]*overflow-y:\s*auto/s);
  assert.match(css, /html, body\s*\{[^}]*min-width:\s*320px/s);
});

test("theme-color metadata exists before the first-paint preference script", () => {
  for (const path of ["../index.html", "../../desktop/index.html"]) {
    const source = read(path);
    const meta = source.indexOf('<meta name="theme-color"');
    const script = source.indexOf("<script>");
    assert.ok(meta >= 0 && script > meta, `${path} must declare theme-color before applying the saved appearance.`);
    assert.match(source, /color\.content = theme === "dark" \? "#0c0f16" : "#f4f6fa"/);
  }
});

test("workspace secondary text palettes meet WCAG AA for normal-size labels", () => {
  const css = read("../src/styles.css");
  const color = (hex: string) => {
    const value = hex.replace("#", "");
    return [0, 2, 4].map(offset => parseInt(value.slice(offset, offset + 2), 16) / 255)
      .map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
  };
  const contrast = (foreground: string, background: string) => {
    const f = color(foreground), b = color(background);
    const luminance = (channels: number[]) => channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    const [high, low] = [luminance(f), luminance(b)].sort((a, b) => b - a);
    return (high + 0.05) / (low + 0.05);
  };
  assert.match(css, /html\[data-theme="dark"\] \.app-shell:not\(\.light\)\s*\{[^}]*--faint:\s*#8996aa/s);
  assert.match(css, /\.app-shell\.light\s*\{[^}]*--faint:\s*#626f84/s);
  assert.ok(contrast("#8996aa", "#141a25") >= 4.5, "Dark secondary labels need 4.5:1 contrast.");
  assert.ok(contrast("#626f84", "#f4f6fa") >= 4.5, "Light secondary labels need 4.5:1 contrast.");
});

test("workspace styles retain one authoritative appearance palette", () => {
  const css = read("../src/styles.css");
  const desktop = read("../../desktop/src/desktop.css");
  assert.ok(!css.includes("--accent:#ff9a52"));
  assert.ok(!css.includes("--accent:#e96f23"));
  assert.ok(css.includes(".app-shell.light{color-scheme:light;--bg:#f4f6fa"));
  assert.ok(desktop.includes("--muted: #5f6b80;"));
  assert.ok(desktop.includes("--faint: #626f84;"));
  assert.ok(desktop.includes("--muted: #aab6ca;"));
  assert.ok(desktop.includes("--faint: #8996aa;"));
});


test("QR station poster renders one canonical QR in an isolated single-page A5 print surface", () => {
  const app = read("../src/App.tsx");
  const poster = read("../src/QrStationPrintDocument.tsx");
  const css = read("../src/styles.css");

  assert.match(app, /<QrStationPrintDocument station=\{stations\.find\(station => station\.id === printingStationId\)/);
  assert.match(app, /querySelectorAll<HTMLImageElement>\("\.qr-print-document img"\)/);
  assert.match(poster, /createPortal\([\s\S]*document\.body/);
  assert.match(poster, /<QRCode value=\{station\.publicUrl\}/);
  assert.equal((poster.match(/<QRCode\b/g) ?? []).length, 1);
  assert.match(poster, /Scan, save &amp; send/);
  assert.match(poster, /save it in the app/i);
  assert.match(poster, /We review your files/);
  assert.match(poster, /Print &amp; pick up/);
  assert.match(poster, /src="\/branding\/quantaedge-logo\.svg" alt="QuantaEdge/);
  assert.match(css, /@page\s*\{\s*size:\s*A5 landscape;\s*margin:\s*0;/);
  assert.doesNotMatch(css, /size: 90mm 54mm/);
  assert.match(css, /body > #root,\s*body > #smartprint-desktop-root\s*\{\s*display:\s*none\s*!important;/,
    "Print mode must hide both React roots while preserving the isolated station QR poster.");
  assert.match(css, /body > \.qr-print-document\s*\{[^}]*width:\s*210mm[^}]*height:\s*148mm/s);
  assert.match(css, /\.qr-print-code-frame svg\s*\{[^}]*width:\s*66mm[^}]*height:\s*66mm/s);
});


test("QR station poster has a clear three-step hierarchy and scan-first customer call to action", () => {
  const poster = read("../src/QrStationPrintDocument.tsx");
  const css = read("../src/styles.css");

  assert.match(poster, /YOUR PRINTING, MADE SIMPLE/);
  assert.match(poster, /Scan, save &amp; send/);
  assert.match(poster, /We review your files/);
  assert.match(poster, /Print &amp; pick up/);
  assert.match(poster, /START HERE/);
  assert.match(poster, /SCAN TO PRINT/);
  assert.match(poster, /quantaedge-logo\.svg/);
  assert.match(css, /qr-print-step-scan/);
  assert.match(css, /qr-print-step-review/);
  assert.match(css, /qr-print-step-collect/);
  assert.match(css, /qr-print-scan-panel\s*\{/);
  assert.match(css, /font:\s*500 7\.8pt\/1\.28/);
  assert.match(poster, /aria-hidden="true">↓<\/span>/);
});

test("batch routing enforces local-computer ownership and preserves print-time audit identity", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const css = read("../src/styles.css");

  assert.match(app, /Assign one printer for all files in batch/);
  assert.match(app, /One printer is used for all active files/);
  assert.match(app, /pages requested/);
  assert.match(app, /Save as PDF/);
  assert.match(app, /Saves a PDF, not paper/);
  assert.match(app, /requiredLocalAgentId/);
  assert.match(app, /localComputerIdentityConflict/);
  assert.match(app, /lockedPrinterOnWrongComputer/);
  assert.match(app, /Shop computer:/);
  assert.match(app, /Paper output manually confirmed by/);
  assert.match(app, /Cloud-assisted upload is now selected/);
  assert.match(app, /attached to its original job/);
  assert.match(app, /resume that same upload without creating another print job/);
  assert.match(api, /local-upload-tickets.*cloud-recovery/);
  assert.doesNotMatch(app, /aria-label=\{"Assign printer for "\+job\.jobNumber\}/);
  assert.match(api, /printerAgentName\?: string \| null/);
  assert.match(api, /outputConfirmedByName\?: string \| null/);
  assert.match(api, /documentAgentDeviceId\?: string \| null/);
  assert.match(css, /\.queue-batch-file-printer/);
  assert.match(css, /\.printer-duplicate-warning/);
  assert.match(css, /\.queue-batch-assignment-note\{font-size:12px/);
});

test("customer upload form clearly supports backend-approved image formats", () => {
  const app = read("../src/App.tsx");
  const customerFlowStart = app.indexOf("function CustomerPrintFlow(");
  const customerFlowEnd = app.indexOf("function UploadIcon()", customerFlowStart);
  assert.ok(customerFlowStart >= 0 && customerFlowEnd > customerFlowStart,
    "The shared customer upload form must remain present.");
  const customerFlow = app.slice(customerFlowStart, customerFlowEnd);
  assert.ok(customerFlow.includes("Add documents or images"));
  assert.ok(customerFlow.includes("Select documents or images"));
  assert.ok(customerFlow.includes('accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png"'));
  assert.ok(app.includes('const supported=rootFiles.filter(candidate=>/\\.(pdf|jpe?g|png)$/i.test(candidate.name));'),
    "The shared selection handler must retain PDF, JPG, JPEG and PNG support.");
});

test("Android customer printing uses the same single Copies control and event handlers as the browser", () => {
  const app = read("../src/App.tsx");
  const customerFlowStart = app.indexOf("function CustomerPrintFlow(");
  const customerFlowEnd = app.indexOf("function UploadIcon()", customerFlowStart);
  assert.ok(customerFlowStart >= 0 && customerFlowEnd > customerFlowStart,
    "The shared CustomerPrintFlow component must remain present.");
  const customerFlow = app.slice(customerFlowStart, customerFlowEnd);
  assert.ok(app.includes("<CustomerPrintFlow dark={dark} publicCode={publicCode}"), "Customer print flow follows the saved appearance.");
  assert.ok(customerFlow.includes('navigator.userAgent.includes("SmartPrintAndroid/1")'));
  assert.equal((customerFlow.match(/customer-folder-copies/g) ?? []).length, 2,
    "There must be one folder-default Copies input and one per-file Custom override in the shared flow.");
  assert.ok(customerFlow.includes("onChange={event=>{"));
  assert.ok(customerFlow.includes("onBlur={event=>{"));
  const copiesStart = customerFlow.indexOf('<label className="customer-folder-copies">');
  const copiesEnd = customerFlow.indexOf("</label>", copiesStart);
  const copiesInput = customerFlow.slice(copiesStart, copiesEnd);
  assert.doesNotMatch(copiesInput, /nativeAppMode/,
    "Android mode may change presentation, but must not fork Copies behaviour.");
});

test("shop connection guidance keeps hotspot setup clear for owners", () => {
  const app = read("../src/App.tsx");
  assert.match(app, /Choose the Wi-Fi or hotspot customers will use to send documents/);
  assert.match(app, /Shop Wi-Fi or hotspot/);
  assert.match(app, /Check with another phone/);
  assert.match(app, /Connect another phone to this same Wi-Fi or hotspot/);
  assert.match(app, /phone sharing the hotspot cannot join it/);
  assert.match(app, /This screen updates automatically when the phone connects/);
  assert.match(app, /Check that the second phone is on the selected Wi-Fi or hotspot/);
  assert.match(app, /Some hotspots block devices from communicating; try shop Wi-Fi instead/);
  assert.match(app, /Connect this computer to the shop Wi-Fi or hotspot, then refresh/);
  assert.doesNotMatch(app, /Connect this computer to the Wi-Fi or network you use at the shop/);
});

test("customer local-upload recovery explains shop Wi-Fi and hotspot without adding setup steps", () => {
  const app = read("../src/App.tsx");
  assert.match(app, /same Wi-Fi or hotspot as the shop computer/);
  assert.match(app, /same shop Wi-Fi or hotspot/);
  assert.match(app, /Direct shop Wi-Fi or hotspot transfer is temporarily unavailable/);
  assert.match(app, /Shop connection ready/);
});


test("shop owner navigation groups daily work and moves account destinations into the header menu", () => {
  const app = read("../src/App.tsx");
  const css = read("../src/styles.css");
  const sidebarStart = app.indexOf('<aside className={sidebarOpen');
  const sidebarEnd = app.indexOf("</aside>", sidebarStart);
  assert.ok(sidebarStart >= 0 && sidebarEnd > sidebarStart, "The shop navigation sidebar must remain present.");
  const sidebar = app.slice(sidebarStart, sidebarEnd);

  assert.match(app, /const sectionGroups:/);
  assert.match(sidebar, /sectionGroups\.map/);
  assert.doesNotMatch(sidebar, /plan-card|support-link|profile-row/,
    "Trial, help and owner controls must not be permanently pinned in the sidebar.");
  assert.match(app, /Personal details/);
  assert.match(app, /Shop profile &amp; branding/);
  assert.match(app, /Trial &amp; subscription/);
  assert.match(app, /Help &amp; support/);
  assert.match(app, /accountMenuOpen/);
  assert.ok(app.indexOf("const [accountMenuOpen") < app.indexOf("if (!accountMenuOpen) return"),
    "The account-menu state must be initialized before its dismissal effect reads it.");
  assert.match(app, /HelpSupportModal/);
  assert.match(css, /\.app-shell \.nav-group-items\s*\{/);
});

test("shop workspace content expands into the space left after the sidebar", () => {
  const css = read("../src/styles.css");
  const app = read("../src/App.tsx");

  assert.match(css, /\.app-shell > \.main-panel\s*\{[^}]*flex:\s*1 1 0%/s);
  assert.match(css, /\.app-shell > \.main-panel > \.content-wrap\s*\{[^}]*width:\s*100%[^}]*max-width:\s*1920px/s);
  assert.match(css, /\.app-shell \.customers-search\s*\{[^}]*max-width:\s*none/s);
  assert.match(app, /activePageTitle/);
  assert.match(css, /@media \(max-width: 850px\)/);
  assert.match(css, /@media \(max-width: 600px\)/);
});


test("shop owner personal details are editable from the account menu without mixing shop branding", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const settingsStart = app.indexOf("function SettingsPage");
  const settingsEnd = app.indexOf("function ShopOwnerAccountModal", settingsStart);
  const settings = app.slice(settingsStart, settingsEnd);
  const modalStart = app.indexOf("function ShopOwnerAccountModal");
  const modalEnd = app.indexOf("function HelpSupportModal", modalStart);
  const modal = app.slice(modalStart, modalEnd);

  assert.match(app, /Personal details/);
  assert.match(app, /ownerAccountOpen/);
  assert.match(app, /ShopOwnerAccountModal/);
  assert.match(api, /updateShopOwnerAccount/);
  assert.match(api, /ownerEmail: string/);
  assert.match(modal, /Your name/);
  assert.match(modal, /Sign-in email/);
  assert.match(modal, /Current password/);
  assert.match(modal, /emailChanged/);
  assert.doesNotMatch(modal, /useEffect\(/,
    "The modal must not reset typed email state in a post-mount effect.");
  assert.doesNotMatch(settings, /ownerEmail|Current password/,
    "Personal sign-in details must stay separate from customer-facing shop branding.");
});


test("native customer UI follows explicit SmartPrint theme and marks selected upload mode", () => {
  const css = read("../src/styles.css");
  const app = read("../src/App.tsx");
  assert.doesNotMatch(css, /@media\s*\(prefers-color-scheme:\s*dark\)/,
    "The OS dark preference must not override a saved SmartPrint light theme.");
  assert.match(css, /html\[data-theme="dark"\] \.customer-entry\.smartprint-native/);
  assert.match(css, /\.customer-entry\.smartprint-native \.customer-upload-mode\.selected/);
  assert.match(css, /customer-upload-mode\.selected::after[\s\S]*?Selected/);
  assert.match(app, /aria-pressed=\{uploadMode==="LOCAL"\}/);
  assert.match(app, /aria-pressed=\{uploadMode==="CLOUD"\}/);
});


test("customer print settings are preserved independently of printer capability reports", () => {
  const app = read("../src/App.tsx");
  const flowStart = app.indexOf("function CustomerPrintFlow(");
  const flowEnd = app.indexOf("function UploadIcon()", flowStart);
  assert.ok(flowStart >= 0 && flowEnd > flowStart,
    "The shared customer print flow must remain present.");
  const flow = app.slice(flowStart, flowEnd);

  assert.ok(flow.includes("Customer requests are captured independently of current hardware reports."));
  assert.ok(flow.includes("void printerCapabilitiesJson;"));
  assert.doesNotMatch(flow, /disabled=\{!supportsColour\}|disabled=\{!supportsDuplex\}|a4NotConfirmed/);
  // Settings are derived from the selected file/group defaults, while ID Studio
  // receives a fixed portrait/A4/colour/single-sided profile of its own.
  assert.ok(flow.includes("colourMode:selected.colourMode"));
  assert.ok(flow.includes("isDuplex:selected.isDuplex"));
  assert.ok(flow.includes('paperSize:selected.advancedEnabled?selected.paperSize:"A4"'));
  assert.ok(flow.includes('orientation:selected.advancedEnabled?selected.orientation:"AUTO"'));
  assert.ok(flow.includes("if(isSmartPrintIdDocument(candidate)) return {"));
  assert.ok(flow.includes('copies:1,colourMode:"Colour",isDuplex:false,paperSize:"A4",orientation:"PORTRAIT"'));
});

test("folder defaults inherit to files and advanced paper options are opt-in", () => {
  const app = read("../src/App.tsx");
  const flowStart = app.indexOf("function CustomerPrintFlow(");
  const flowEnd = app.indexOf("function UploadIcon()", flowStart);
  const flow = app.slice(flowStart, flowEnd);

  assert.ok(flow.includes("advancedEnabled:false"));
  assert.ok(flow.includes("const selected=own?.custom?{...defaults,...own}:defaults;"));
  assert.ok(flow.includes("checked={defaults.advancedEnabled}"));
  assert.ok(flow.includes("checked={own.advancedEnabled}"));
  assert.ok(flow.includes("{!isIdStudioGroup&&defaults.advancedEnabled&&"));
  assert.ok(flow.includes("Prepared ID sheet · one colour copy · A4 portrait · single-sided"));
  assert.ok(flow.includes('{defaults.colourMode==="Mono"'));
  assert.ok(flow.includes('Copies · all files'));
});

test("offline requests use the existing secure queue and owner changes require customer approval", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const server = read("../../../src/SmartPrint.Api/Program.cs");

  assert.ok(app.includes("secureQueueFallbackEnabled"));
  assert.ok(server.includes('publicApi.MapPost("/sessions/current/local-upload-tickets/{ticketId:guid}/cloud-recovery"'));
  assert.ok(server.includes("if (!subscription.CloudAssistedPrintingEnabled && recoveryAgentAvailable)"));
  assert.doesNotMatch(api, /X-Print-Queue-Fallback|allowQueueFallback/);
  assert.doesNotMatch(server, /X-Print-Queue-Fallback|queueFallbackRequested/);
  assert.ok(server.includes('shop.MapPatch("/print-jobs/{jobId:guid}/settings"'));
  assert.ok(server.includes('job.Status = "AWAITING_CUSTOMER_APPROVAL";'));
  assert.ok(app.includes("onUpdateSettings={updatePrintJobSettings}"));
});

test("customer paper summaries reflect advanced folder and per-file choices", () => {
  const app = read("../src/App.tsx");
  assert.match(app, /customer-paper-default">\{defaults\.advancedEnabled\?defaults\.paperSize:"A4"\}/);
  assert.match(app, /customer-paper-default">\{own\.advancedEnabled\?own\.paperSize:"A4"\}/);
  assert.match(app, /entry\.quote!\.paperSize/);
  assert.match(app, /item\.job\.paperSize\?\?"A4"/);
});

test("all print settings controls capture React event values before state updaters", () => {
  const app = read("../src/App.tsx");
  const setters = ["setGroupSettings(current=>","setFileSettings(current=>","setEditingPrintSettings(current=>"];
  for (const setter of setters) {
    const lines = app.split("\n").filter(line => line.includes(setter));
    assert.ok(lines.length > 0, `${setter} must remain part of the shared print settings flow.`);
    assert.ok(lines.every(line => !line.includes("event.currentTarget") && !line.includes("event.target")),
      `${setter} must use values captured before entering the state updater.`);
  }
  assert.match(app, /const advancedEnabled=event\.currentTarget\.checked/);
  assert.match(app, /const paperSize=event\.currentTarget\.value/);
  assert.match(app, /const orientation=event\.currentTarget\.value/);
  assert.match(app, /const copies=Math\.max\(1,Math\.min\(100,Number\(event\.currentTarget\.value\)\|\|1\)\)/);
});


test("PDF footer branding is opt-in per batch and cannot cover source content or paper output", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const server = read("../../../src/SmartPrint.Api/Program.cs");
  const contracts = read("../../../src/SmartPrint.Api/Contracts/ApiContracts.cs");
  const agent = read("../../../src/SmartPrint.Agent/WindowsPrintExecutor.cs");
  const pdfOverlay = read("../../../src/SmartPrint.Agent/PdfBrandingOverlay.cs");
  const queueModels = read("../../../src/SmartPrint.Agent/AgentQueueModels.cs");
  const safetyRules = read("../../../src/SmartPrint.Agent/PdfBrandingRules.cs");

  assert.ok(app.includes('useState(()=>!hasIdDocument&&jobs.some(job=>job.pdfBrandingEnabled===true))'));
  assert.ok(app.includes('checked={pdfBrandingEnabled&&!hasIdDocument} disabled={busy||hasIdDocument}'));
  assert.ok(api.includes('body: JSON.stringify({ reviewedJobIds, acceptedJobIds, pdfBrandingEnabled })'));
  assert.ok(contracts.includes('bool PdfBrandingEnabled = false'));
  assert.ok(server.includes('request.PdfBrandingEnabled'));
  assert.ok(server.includes('job.PdfBrandingEnabled = request.PdfBrandingEnabled'));
  assert.match(server, /foreach \(var job in lockedJobs\)\s*\{\s*if \(job\.PdfBrandingEnabled/);
  assert.doesNotMatch(server, /foreach \(var job in allBatchJobs\)\s*\{\s*if \(job\.PdfBrandingEnabled/);
  assert.ok(server.includes('candidate.PdfBrandingEnabled'));
  assert.ok(queueModels.includes('bool PdfBrandingEnabled = false'));
  assert.ok(agent.includes('"SMARTPRINT_BRANDED_PDF_RENDERER"'));
  assert.ok(agent.includes('PdfBrandingOverlay.TryCreate(entry, bytes, pdfRaster.HasDigitalSignatures, pdfRaster.HasInteractiveForms, cancellationToken)'));
  assert.ok(agent.includes('Never raster-paint a fallback PDF'));
  assert.ok(!agent.includes('ApplySmartPrintFooter(page'));
  assert.ok(pdfOverlay.includes('PdfBrandingRules.HasSafeFooterBand(packed, width, height, bandHeight)'));
  assert.ok(safetyRules.includes('RequiredBlankBandPoints = 20f'));
  assert.ok(server.includes('item.PdfBrandingEnabled'));
  assert.ok(app.includes('jobStatus.pdfBrandingEnabled&&<div className="customer-branding-note">'));
  assert.ok(app.includes('Footer only appears on PDFs with a verified blank bottom margin.'));
  assert.ok(app.includes('Digitally signed or interactive PDFs remain unchanged.'));
});


test("contextual information stays concise and is accessible by hover, focus and tap", () => {
  const app = read("../src/App.tsx");
  const css = read("../src/styles.css");
  assert.match(app, /function InfoTooltip/);
  assert.match(app, /aria-describedby=\{tooltipId\}/);
  assert.match(app, /onMouseEnter=\{\(\)=>setOpen\(true\)\}/);
  assert.match(app, /onClick=\{\(\)=>setOpen\(true\)\}/);
  assert.match(app, /Files stay on this computer\. Reconnect it to use its printers\./);
  assert.match(app, /To switch computers, upload a new Cloud-assisted batch\./);
  assert.match(app, /jobStatus\.pdfBrandingEnabled&&<div className="customer-branding-note">/);
  assert.doesNotMatch(app, /If this request is saved as a PDF, a small SmartPrint by QuantaEdge footer/);
  assert.match(css, /\.context-info:focus-within \.context-info-tooltip/);
  assert.match(css, /\.context-info\.context-info-open \.context-info-tooltip/);
});

test("customer payment settings refresh without reopening the QR session and explain disabled cash", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const server = read("../../../src/SmartPrint.Api/Program.cs");
  assert.match(app, /paymentOptions:session\.paymentOptions/);
  assert.match(app, /Cash at counter unavailable/);
  assert.match(app, /Disabled · customers will not see cash at the counter\./);
  assert.match(api, /paymentOptions: \{ upiEnabled: boolean; upiId\?: string \| null; payeeName\?: string \| null; cashEnabled: boolean; instantPaymentEnabled: boolean \}/);
  assert.match(server, /var paymentOptions = await db\.ShopPaymentProfiles\.AsNoTracking\(\)/);
  assert.match(server, /PaymentOptions = paymentOptions/);
  assert.match(server, /CashEnabled = false/);
});

test("Windows local document previews prefer the paired Agent loopback endpoint", () => {
  const api = read("../src/api.ts");
  const routing = read("../src/local-preview-routing.ts");
  const desktop = read("../../desktop/src-tauri/tauri.conf.json");
  assert.ok(routing.includes("function isPrivateLocalPreviewEndpoint(value: string, allowLoopback = false)"));
  assert.ok(routing.includes('loopbackIpv4 = endpoint.hostname === "127.0.0.1"'));
  assert.ok(routing.includes('LOCAL_AGENT_PREVIEW_ENDPOINT = "http://127.0.0.1:17842"'));
  assert.ok(api.includes("getLocalPreviewEndpointCandidates(isDesktopApp(), source.endpoints)"));
  assert.ok(api.includes("isPrivateLocalPreviewEndpoint(value, isDesktopApp())"));
  assert.ok(desktop.includes("http://127.0.0.1:17842"));
  assert.ok(api.includes("The paired shop computer did not respond. Check that the Windows Print Agent is running and connected"));
  assert.ok(api.includes("Could not connect to the paired shop computer. Check that the Windows Print Agent is running and allowed through Windows Firewall"));
});

test("customer uploader is centred and shared light-theme text tokens meet readable contrast", () => {
  const css = read("../src/styles.css");
  const uploaderThemeGuard = css.slice(css.indexOf("Theme foreground and surface must change together"));
  assert.ok(uploaderThemeGuard.includes(".customer-entry:not(.smartprint-native) .customer-upload-zone {"));
  assert.ok(uploaderThemeGuard.includes("transition: border-color .18s;"));
  const desktop = read("../../desktop/src/desktop.css");
  assert.match(css, /customer-entry-card--upload \.customer-print-flow\{width:min\(100%,1040px\);margin-inline:auto\}/);
  assert.match(css, /customer-flow-heading\{align-items:center;justify-content:center;flex-direction:column/);
  assert.match(css, /customer-upload-picker-grid\{width:min\(100%,860px\);margin-inline:auto\}/);
  assert.match(css, /--faint:#5f6b80/);
  assert.match(css, /--muted:#59677e/);
  assert.match(desktop, /--faint: #5f6b80;/);
  assert.match(desktop, /--muted: #59677e;/);
});


test("native Windows preview tries loopback first and retains advertised LAN fallback", () => {
  const advertised = ["http://192.168.1.20:17842/", "http://10.0.0.8:17842/"];
  assert.deepEqual(getLocalPreviewEndpointCandidates(true, advertised), ["http://127.0.0.1:17842", ...advertised]);
  assert.equal(isPrivateLocalPreviewEndpoint("http://127.0.0.1:17842", true)?.origin, "http://127.0.0.1:17842");
});

test("browser preview cannot add or target loopback, and public endpoints are rejected", () => {
  const advertised = ["http://192.168.1.20:17842/"];
  assert.deepEqual(getLocalPreviewEndpointCandidates(false, advertised), advertised);
  assert.equal(isPrivateLocalPreviewEndpoint("http://127.0.0.1:17842", false), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://8.8.8.8:17842/"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://localhost:17842/"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("https://192.168.1.20:17842/"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://192.168.1.20:17842/extra"), null);
  assert.equal(isPrivateLocalPreviewEndpoint("http://user@192.168.1.20:17842/"), null);
});

test("local preview endpoint candidates are deduplicated and bounded", () => {
  const advertised = Array.from({length: 10}, (_, index) => `http://192.168.1.${index + 1}:17842/`);
  const candidates = getLocalPreviewEndpointCandidates(true, [advertised[0], ...advertised]);
  assert.equal(candidates.length, 8);
  assert.equal(candidates[0], "http://127.0.0.1:17842");
  assert.equal(candidates.filter(value => value === advertised[0]).length, 1);
});

test("session renewal refreshes customer payment choices and explains unavailable payment methods", () => {
  const app = read("../src/App.tsx");
  const api = read("../src/api.ts");
  const backend = read("../../../src/SmartPrint.Api/Program.cs");
  assert.match(api, /paymentOptions: \{ upiEnabled: boolean; upiId\?: string \| null; payeeName\?: string \| null; cashEnabled: boolean; instantPaymentEnabled: boolean \}/);
  assert.match(app, /paymentOptions:session\.paymentOptions/);
  assert.match(backend, /PaymentOptions = paymentOptions/);
  assert.equal((app.match(/The shop has not enabled UPI or cash payment/g) ?? []).length, 2);
});

test("contextual help supports outside-click and Escape dismissal", () => {
  const app = read("../src/App.tsx");
  assert.match(app, /document\.addEventListener\("pointerdown",dismissOutside\)/);
  assert.match(app, /event\.key==="Escape"/);
  assert.match(app, /aria-expanded=\{open\}/);
});


test("SmartPrint uses the full theme-aware brand lockup inside the app", () => {
  const component = read("../src/BrandLockup.tsx");
  const css = read("../src/styles.css");
  const light = read("../public/branding/smartprint-logo-light.svg");
  const dark = read("../public/branding/smartprint-logo-dark.svg");
  const app = read("../src/App.tsx");

  assert.match(component, /smartprint-logo-light\.svg/);
  assert.match(component, /smartprint-logo-dark\.svg/);
  assert.match(component, /smartprint-mark\.svg/);
  assert.doesNotMatch(component, /quantaedge-logo\.svg/,
    "The parent logo must not be inserted beside the SmartPrint wordmark.");
  assert.match(light, /fill="#7258E8">Print/);
  assert.match(light, /fill="#152038">Quanta/);
  assert.match(light, /fill="#F97316" stroke="#913B0B">Edge/);
  assert.match(dark, /fill="#F0F3FA">Smart/);
  assert.match(dark, /fill="#9B8CFF">Print/);
  assert.match(dark, /fill="#152038" stroke="#D7DEEE"/);
  assert.doesNotMatch(light, /<rect x="202"|<path d="M298 104/,
    "QuantaEdge must not sit on white, navy, blue, or pill-shaped backing panels.");
  assert.doesNotMatch(dark, /<rect x="202"|<path d="M298 104/,
    "Dark mode must not add a background behind QuantaEdge either.");
  assert.match(light, /font-size="27" fill="#152038">Quanta/);
  assert.match(dark, /font-size="27" fill="#152038" stroke="#D7DEEE"/);
  assert.match(dark, /fill="#F97316">Edge/);
  assert.match(css, /html\[data-theme="dark"\] \.brand-lockup-logo-dark/);
  assert.match(css, /app-shell\.sidebar-collapsed \.brand-lockup \.brand-mark-image/);
  assert.match(app, /<BrandLockup className="boot-brand-lockup" \/>/);
});

test("Android and Windows launchers use the clean icon-only identity and Smart Print app name", () => {
  const androidStrings = read("../../android/app/src/main/res/values/strings.xml");
  const androidForeground = read("../../android/app/src/main/res/drawable/ic_launcher_foreground.xml");
  const androidMonochrome = read("../../android/app/src/main/res/drawable/ic_launcher_monochrome.xml");
  const appIcon = read("../public/branding/smartprint-app-icon.svg");
  const manifest = JSON.parse(read("../public/manifest.webmanifest"));
  const tauri = JSON.parse(read("../../desktop/src-tauri/tauri.conf.json"));
  const desktopHtml = read("../../desktop/index.html");

  assert.match(androidStrings, /<string name="app_name">Smart Print<\/string>/);
  assert.doesNotMatch(androidForeground, /SmartPrint|QuantaEdge|<text/i);
  assert.doesNotMatch(androidMonochrome, /SmartPrint|QuantaEdge|<text/i);
  assert.doesNotMatch(appIcon, /<text|QuantaEdge/i);
  assert.equal(manifest.name, "Smart Print");
  assert.equal(manifest.short_name, "Smart Print");
  assert.equal(tauri.productName, "Smart Print");
  assert.equal(tauri.app.windows[0].title, "Smart Print");
  assert.match(desktopHtml, /<title>Smart Print<\/title>/);
});

test("SmartPrint and QuantaEdge lockup lettering has AA contrast in both themes", () => {
  const contrast = (foreground: string, background: string) => {
    const luminance = (hex: string) => {
      const channels = [0, 2, 4].map(offset => parseInt(hex.slice(offset + 1, offset + 3), 16) / 255)
        .map(channel => channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
      return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    };
    const [high, low] = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
    return (high + 0.05) / (low + 0.05);
  };

  const pairs = [
    ["#152038", "#F4F6FA", "light Smart"],
    ["#7258E8", "#F4F6FA", "light Print"],
    ["#667085", "#F4F6FA", "light by"],
    ["#F0F3FA", "#141A25", "dark Smart"],
    ["#9B8CFF", "#141A25", "dark Print"],
    ["#AAB6CA", "#141A25", "dark by"],
    ["#152038", "#F5F7FC", "light Quanta"],
    ["#913B0B", "#F4F6FA", "light Edge outline"],
    ["#D7DEEE", "#141A25", "dark Quanta outline"],
    ["#F97316", "#141A25", "dark Edge"],
  ] as const;

  for (const [foreground, background, label] of pairs) {
    assert.ok(contrast(foreground, background) >= 4.5,
      label + " lettering must remain readable (WCAG AA); got " +
      contrast(foreground, background).toFixed(2) + ":1.");
  }
});
