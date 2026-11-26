import {
  ArrowRight, ArrowUpRight, Check, ChevronRight, Cloud, Download, FileCheck2, FileText,
  LockKeyhole, Monitor, Printer, QrCode, RotateCcw, ShieldCheck, Smartphone, Store, Wifi, X
} from "lucide-react";
import { getPublicDownloadPath, usePublicRelease } from "./release-metadata";
import { useFileDownload } from "./download-manager";
import ThemeToggleButton from "./ThemeToggleButton";
import BrandLockup from "./BrandLockup";
import "./public-landing.css";
import { useLocale, translateUi, translateUiText } from "./i18n";

export default function PublicLandingPage({ dark, onToggleTheme }: { dark: boolean; onToggleTheme: () => void }) {
  const { locale } = useLocale();
  const t = (value: string) => translateUiText(locale, value);
  const { release, state: releaseState, refresh } = usePublicRelease();
  const windowsDownloadUrl = release?.windowsAvailable && release.version
    ? getPublicDownloadPath("windows", release.version, release.downloadBaseUrl)
    : null;
  const androidDownloadUrl = release?.androidAvailable && release.version
    ? getPublicDownloadPath("android", release.version, release.downloadBaseUrl)
    : null;
  const windowsFallbackDownloadUrl = release?.windowsAvailable && release.version
    ? getPublicDownloadPath("windows", release.version)
    : null;
  const androidFallbackDownloadUrl = release?.androidAvailable && release.version
    ? getPublicDownloadPath("android", release.version)
    : null;

  return (
    <div className="sp-public">
      <header className="sp-public-header">
        <a className="sp-public-brand" href="/" aria-label={t("SmartPrint home")}>
          <BrandLockup className="sp-public-brand-lockup" />
        </a>
        <nav className="sp-public-nav" aria-label={t("Main navigation")}>
          <a href="#how-it-works">{t("How it works")}</a>
          <a href="#for-shops">{translateUi(locale,"public.forShops")}</a>
          <a href="#for-customers">{translateUi(locale,"public.forCustomers")}</a>
          <a href="#downloads">{t("Downloads")}</a>
        </nav>
        <ThemeToggleButton dark={dark} onToggle={onToggleTheme} className="sp-theme-toggle"/>
        <a className="sp-shop-entry" href="/shop">
          <span className="sp-shop-entry-icon"><Store size={18}/><Printer size={10}/></span>
          <span>{translateUi(locale,"public.forPrintShops")}</span>
          <ArrowUpRight size={15}/>
        </a>
      </header>

      <main>
        <section className="sp-hero">
          <div className="sp-hero-copy">
            <div className="sp-eyebrow"><span className="sp-eyebrow-dot"/> {t("SMART PRINT BY QUANTAEDGE")}</div>
            <h1>{translateUi(locale,"public.heroTitle")}</h1>
            <p className="sp-hero-lede">{translateUi(locale,"public.heroDescription")}</p>
            <div className="sp-hero-actions">
              <a className="sp-button-primary" href="/shop?mode=register">{translateUi(locale,"public.createShop")} <ArrowRight size={17}/></a>
              <a className="sp-button-quiet" href="#how-it-works">{translateUi(locale,"public.seeHow")} <ChevronRight size={17}/></a>
            </div>
            <div className="sp-hero-trust"><span><ShieldCheck size={15}/> {t("Shop-controlled print approval")}</span><span><LockKeyhole size={14}/> {t("Shop-authorised job handoff")}</span></div>
          </div>

          <div className="sp-hero-art" aria-label={t("Illustration of a customer phone connecting to a print shop")}>
            <div className="sp-art-orbit sp-art-orbit-one"/>
            <div className="sp-art-orbit sp-art-orbit-two"/>
            <div className="sp-art-customer">
              <div className="sp-art-phone-top"><span/><i/></div>
              <div className="sp-art-phone-screen">
                <div className="sp-art-phone-brand"><span><Printer size={11}/></span> smartprint</div>
                <div className="sp-art-document"><FileText size={23}/><div><b>Project-notes.pdf</b><small>{t("12 pages · PDF")}</small></div><Check size={14}/></div>
                <div className="sp-art-upload"><span/><span/><span/></div>
                <div className="sp-art-phone-button">{t("Ready to print")} <ArrowRight size={11}/></div>
              </div>
              <div className="sp-art-phone-caption"><Smartphone size={12}/> {t("CUSTOMER")}</div>
            </div>
            <div className="sp-art-connection"><span/><span/><span/><small>{t("SHOP CONNECTION")}</small></div>
            <div className="sp-art-shop">
              <div className="sp-art-shop-head"><span className="sp-art-shop-icon"><Store size={16}/></span><div><b>{t("Your print shop")}</b><small>{t("Workspace connected")}</small></div><i/></div>
              <div className="sp-art-printer"><span><Printer size={25}/></span><div><b>{t("Shop printer")}</b><small>{t("Ready for the next job")}</small></div><em><Check size={12}/></em></div>
              <div className="sp-art-shop-foot"><span><Wifi size={12}/> Agent online</span><span>{t("Job received")}</span></div>
              <div className="sp-art-shop-caption">{t("YOUR SHOP")}</div>
            </div>
            <div className="sp-art-floating sp-art-floating-one"><QrCode size={16}/><span>{t("Scan to start")}</span></div>
            <div className="sp-art-floating sp-art-floating-two"><Check size={15}/><span>{t("Shop approved")}</span></div>
          </div>
        </section>

        <section className="sp-proof-strip" aria-label={t("SmartPrint workflow highlights")}>
          <div><QrCode/><span><b>{t("One QR code")}</b><small>{t("Connect each counter")}</small></span></div>
          <div><FileCheck2/><span><b>{t("Clear job review")}</b><small>{t("See each print request")}</small></span></div>
          <div><ShieldCheck/><span><b>{t("Shop stays in control")}</b><small>{t("Approve before printing")}</small></span></div>
          <div><Printer/><span><b>{t("Your printers")}</b><small>{t("Use the shop's setup")}</small></span></div>
        </section>

        <section className="sp-section sp-how" id="how-it-works">
          <div className="sp-section-heading">
            <div className="sp-eyebrow">{t("A simple, connected flow")}</div>
            <h2>{t("Three steps. One smoother print experience.")}</h2>
            <p>{t("No scattered file messages. No repeating copy and colour instructions for every document.")}</p>
          </div>
          <div className="sp-steps">
            <article className="sp-step-card">
              <span className="sp-step-number">01</span><div className="sp-step-icon"><QrCode size={24}/></div>
              <h3>{t("Scan & send")}</h3><p>{t("Customers scan your shop's QR code, add their documents and choose their print settings.")}</p>
              <span className="sp-step-tag">{t("CUSTOMER")}</span>
            </article>
            <article className="sp-step-card">
              <span className="sp-step-number">02</span><div className="sp-step-icon"><FileCheck2 size={24}/></div>
              <h3>{t("Review & approve")}</h3><p>{t("Your team reviews the document, checks the quote and payment, and authorises the job.")}</p>
              <span className="sp-step-tag">{t("YOUR SHOP")}</span>
            </article>
            <article className="sp-step-card">
              <span className="sp-step-number">03</span><div className="sp-step-icon"><Printer size={24}/></div>
              <h3>{t("Print & hand over")}</h3><p>{t("The connected Windows Agent routes approved jobs to the selected shop printer for output.")}</p>
              <span className="sp-step-tag">{t("CONNECTED PRINTER")}</span>
            </article>
          </div>
        </section>

        <section className="sp-two-sided">
          <article className="sp-audience-card sp-customer-card" id="for-customers">
            <div className="sp-audience-kicker"><Smartphone size={15}/> {t("FOR CUSTOMERS")}</div>
            <h2>{t("Printing starts")}<br/>{t("where you are.")}</h2>
            <p>Use your phone to connect to a nearby participating shop. Upload documents, review the print details and follow the job without passing files around.</p>
            <ul><li><Check/> {t("Scan the shop's QR code")}</li><li><Check/> {t("Upload supported documents")}</li><li><Check/> {t("Review the quote and track the job")}</li></ul>
            <div className="sp-audience-visual sp-customer-visual"><div className="sp-customer-mini-phone"><div/><span><FileText size={24}/></span><b>Your document</b><small>Ready to submit</small></div><div className="sp-customer-mini-cloud"><Cloud size={34}/><span>Connected to shop</span></div></div>
            <a href="#downloads" className="sp-text-link">{t("Explore the Android app")} <ArrowRight size={16}/></a>
          </article>
          <article className="sp-audience-card sp-shop-card" id="for-shops">
            <div className="sp-audience-kicker"><Store size={15}/> {t("FOR PRINT SHOPS")}</div>
            <h2>{t("Your counter.")}<br/>{t("Your workflow.")}</h2>
            <p>{t("Keep incoming print requests, pricing, printers and shop approvals in one workspace built around the way local print shops operate.")}</p>
            <ul><li><Check/> {t("Manage QR stations and printers")}</li><li><Check/> {t("Review jobs before authorising them")}</li><li><Check/> {t("Keep control of shop payments and output")}</li></ul>
            <div className="sp-audience-visual sp-shop-visual"><div className="sp-shop-mini-window"><div className="sp-window-dots"><i/><i/><i/></div><div className="sp-mini-stat-row"><span/><span/><span/></div><div className="sp-mini-job"><FileText size={15}/><span/><em>IN REVIEW</em></div><div className="sp-mini-job"><Printer size={15}/><span/><em>APPROVED</em></div></div></div>
            <a href="/shop" className="sp-text-link">{t("Open your shop workspace")} <ArrowRight size={16}/></a>
          </article>
        </section>

        <section className="sp-connection-section">
          <div className="sp-connection-copy">
            <div className="sp-eyebrow">{t("BUILT AROUND YOUR SHOP")}</div>
            <h2>{t("Your printers stay at your shop. Your workflow gets connected.")}</h2>
            <p>The SmartPrint Windows Agent connects the shop computer to the workspace. It discovers installed printer queues and receives only jobs authorised by the shop.</p>
            <div className="sp-connection-points"><span><Check/> {t("Your existing Windows printer setup")}</span><span><Check/> {t("Shop-controlled job authorisation")}</span><span><Check/> {t("Local or cloud-assisted document transfer")}</span></div>
          </div>
          <div className="sp-connection-visual"><div className="sp-device-card"><Monitor size={29}/><b>Shop computer</b><small>SmartPrint Agent</small><span className="sp-online"><i/> Connected</span></div><div className="sp-connection-line"><span/><span/><span/></div><div className="sp-device-printer"><Printer size={35}/><b>Installed printer</b><small>Shop's printer queue</small></div><div className="sp-secure-pill"><LockKeyhole size={12}/> Authorised connection</div></div>
        </section>

        <section className="sp-download-section" id="downloads">
          <div className="sp-section-heading">
            <div className="sp-eyebrow">{t("GET STARTED WITH SMARTPRINT")}</div>
            <h2>{t("The right app for the right job.")}</h2>
            <p>{t("Set up the shop computer or bring the customer experience to Android.")}</p>
          </div>
          <div className="sp-download-grid">
            <article className="sp-download-card">
              <div className="sp-download-icon sp-windows-icon"><Monitor size={25}/></div>
              <div className="sp-download-label-row">
                <div className="sp-download-platform">{t("FOR SHOP OWNERS · WINDOWS 10/11")}</div>
                <span className={windowsDownloadUrl ? "sp-download-tag sp-download-tag-available" : "sp-download-tag sp-download-tag-coming"}>
                  {releaseState === "loading" ? "Checking" : releaseState === "error" ? "Temporarily unavailable" : windowsDownloadUrl ? "Available" : "Coming soon"}
                </span>
              </div>
              <h3>{t("SmartPrint for Windows")}</h3><p>{t("Install the shop Agent on the computer connected to your printers. The guided setup will help connect it to your shop workspace.")}</p>
              <DownloadAction platform="windows" label="Download Windows installer" available={Boolean(windowsDownloadUrl)} downloadUrl={windowsDownloadUrl} fallbackDownloadUrl={windowsFallbackDownloadUrl} releaseState={releaseState} onRefresh={refresh}/>
              <small className="sp-download-note">{t("Windows x64 · Guided installation · Official stable release")}</small>
              <p className="sp-download-security-note">The interim Windows beta installer is unsigned. Windows may show an Unknown Publisher or SmartScreen prompt; some managed devices may block unsigned installers.</p>
            </article>
            <article className="sp-download-card">
              <div className="sp-download-icon sp-android-icon"><Smartphone size={25}/></div>
              <div className="sp-download-label-row">
                <div className="sp-download-platform">{t("FOR CUSTOMERS · ANDROID")}</div>
                <span className={androidDownloadUrl ? "sp-download-tag sp-download-tag-available" : "sp-download-tag sp-download-tag-coming"}>
                  {releaseState === "loading" ? "Checking" : releaseState === "error" ? "Temporarily unavailable" : androidDownloadUrl ? "Available" : "Coming soon"}
                </span>
              </div>
              <h3>{t("SmartPrint for Android")}</h3><p>{t("Scan shop QR codes, organise documents in your library and send files through the connected SmartPrint customer journey.")}</p>
              <DownloadAction platform="android" label="Download signed Android APK" available={Boolean(androidDownloadUrl)} downloadUrl={androidDownloadUrl} fallbackDownloadUrl={androidFallbackDownloadUrl} releaseState={releaseState} onRefresh={refresh}/>
              <small className="sp-download-note">{t("Android 8.0 or later · Production-signed APK")}</small>
              <p className="sp-release-pending">{androidDownloadUrl
                ? `Latest stable release: ${release?.version ?? "published"}.`
                : releaseState === "error"
                  ? "Download information is temporarily unavailable. Retry to check again."
                  : "The signed APK download appears automatically when the latest stable release contains the verified Android package."}</p>
            </article>
          </div>
          <p className="sp-download-footnote"><ShieldCheck size={14}/> {t("Downloads are delivered securely from this SmartPrint website.")}</p>
        </section>

        <section className="sp-final-cta">
          <div><div className="sp-eyebrow">{t("READY TO CONNECT YOUR SHOP?")}</div><h2>{t("Give every print job")}<br/>{t("a clearer way forward.")}</h2><p>{t("Set up your shop workspace and bring your customers into the flow.")}</p></div>
          <a className="sp-button-light" href="/shop?mode=register">{t("Create your shop account")} <ArrowRight size={17}/></a>
        </section>
      </main>
      <footer className="sp-public-footer">
        <a className="sp-public-brand" href="/"><BrandLockup className="sp-public-brand-lockup" /></a>
        <span>© {new Date().getFullYear()} SmartPrint by QuantaEdge</span>
        <div><a href="#how-it-works">{t("How it works")}</a><a href="#downloads">{t("Downloads")}</a><a href="/shop">{t("For print shops")}</a></div>
      <nav className="sp-public-footer-resources" aria-label={t("Print shop resources")}>
          <span>{t("PRINT SHOP RESOURCES")}</span>
          <a href="/features/">{t("All features")}</a>
          <a href="/whatsapp-printing-alternative/">{t("Chat-based print orders")}</a>
          <a href="/document-privacy-private-printing/">{t("Document privacy")}</a>
          <a href="/photo-id-printing/">{t("Photo and ID printing")}</a>
          <a href="/print-shop-software/">{t("Print shop software")}</a>
          <a href="/qr-code-print-ordering/">{t("QR code ordering")}</a>
          <a href="/print-job-management/">{t("Print job management")}</a>
          <a href="/windows-print-agent/">{t("Windows Print Agent")}</a>
          <a href="/smartprint-faq/">{t("SmartPrint FAQ")}</a>
          <a href="/qr-printing-for-xerox-shops/">{t("QR printing for Xerox shops")}</a>
        </nav>
      </footer>
    </div>
  );
}



type DownloadPlatform = "windows" | "android";

function formatDownloadBytes(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return "0 B";
  if (value < 1000) return `${Math.floor(value)} B`;
  const units = ["KB", "MB", "GB"];
  let size = value / 1000;
  let index = 0;
  while (size >= 1000 && index < units.length - 1) {
    size /= 1000;
    index += 1;
  }
  return `${size.toFixed(size >= 10 ? 1 : 2)} ${units[index]}`;
}

function formatDownloadSpeed(value: number): string {
  if (!Number.isFinite(value) || value <= 0) return "Measuring speed…";
  if (value < 1000) return `${Math.round(value)} B/s`;
  if (value < 1_000_000) return `${(value / 1000).toFixed(1)} KB/s`;
  return `${(value / 1_000_000).toFixed(2)} MB/s`;
}

function formatDownloadRemaining(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds) || seconds < 0) return "Time remaining unavailable";
  const total = Math.ceil(seconds);
  if (total < 60) return `About ${total}s remaining`;
  const minutes = Math.floor(total / 60);
  const remainingSeconds = total % 60;
  return remainingSeconds > 0 ? `About ${minutes}m ${remainingSeconds}s remaining` : `About ${minutes}m remaining`;
}

function DownloadAction({
  platform,
  label,
  available,
  downloadUrl,
  fallbackDownloadUrl,
  releaseState,
  onRefresh
}: {
  platform: DownloadPlatform;
  label: string;
  available: boolean;
  downloadUrl: string | null;
  fallbackDownloadUrl: string | null;
  releaseState: "loading" | "ready" | "error";
  onRefresh: () => Promise<void>;
}) {
  const { locale } = useLocale();
  const t = (value: string) => translateUiText(locale, value);
  const fileName = platform === "windows" ? "SmartPrint-Windows-Setup.exe" : "SmartPrint-Android.apk";
  const { download, start, cancel } = useFileDownload(fileName);
  const resolvedDownloadUrl = downloadUrl;
  const percent = download.totalBytes && download.totalBytes > 0
    ? Math.min(100, Math.floor((download.bytesReceived / download.totalBytes) * 100))
    : null;
  const remainingSeconds = download.totalBytes !== null && download.bytesPerSecond > 0
    ? Math.max(0, (download.totalBytes - download.bytesReceived) / download.bytesPerSecond)
    : null;

  if (!available || !resolvedDownloadUrl) {
    if (releaseState === "error") {
      return <button className="sp-download-unavailable sp-download-button" type="button" onClick={() => void onRefresh()}>{t("Retry download availability")}</button>;
    }
    return <span className="sp-download-unavailable sp-download-button" aria-disabled="true">{releaseState === "loading" ? t("Checking download availability…") : platform === "windows" ? t("Windows installer not published") : t("Signed APK not published")}</span>;
  }

  if (download.status === "downloading") {
    return (
      <div className="sp-download-progress" role="group" aria-label={`${label} progress`}>
        <div className="sp-download-progress-heading">
          <span><span className="sp-download-live-dot"/> {t("Downloading")}</span>
          <strong>{percent === null ? t("In progress") : `${percent}%`}</strong>
        </div>
        <div className="sp-download-progress-track" role="progressbar" aria-label={t("{fileName} download progress").replace("{fileName}", fileName)} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent ?? undefined}>
          <span className={percent === null ? "sp-download-progress-fill sp-download-progress-indeterminate" : "sp-download-progress-fill"} style={percent === null ? undefined : { width: `${percent}%` }}/>
        </div>
        <div className="sp-download-progress-details">
          <span>{formatDownloadBytes(download.bytesReceived)} {t("downloaded")}</span>
          <span>{download.totalBytes ? `of ${formatDownloadBytes(download.totalBytes)}` : t("Total size unavailable")}</span>
        </div>
        <div className="sp-download-speed-details">
          <span>{formatDownloadSpeed(download.bytesPerSecond)}</span>
          <span>{formatDownloadRemaining(remainingSeconds)}</span>
        </div>
        <button className="sp-download-cancel" type="button" onClick={cancel}><X size={14}/> {t("Cancel download")}</button>
      </div>
    );
  }

  if (download.status === "complete") {
    return (
      <div className="sp-download-result sp-download-result-success" role="status">
        <div><Check size={15}/><span>{t("Download complete")}</span><strong>{formatDownloadBytes(download.bytesReceived)}</strong></div>
        <button type="button" onClick={() => void start(resolvedDownloadUrl, fallbackDownloadUrl)}><Download size={14}/> {t("Download again")}</button>
      </div>
    );
  }

  if (download.status === "error" || download.status === "cancelled") {
    return (
      <div className={download.status === "error" ? "sp-download-result sp-download-result-error" : "sp-download-result"}>
        <p role="status">{download.status === "cancelled" ? "Download cancelled." : download.errorMessage ?? "The download could not be completed."}</p>
        <button type="button" onClick={() => void start(resolvedDownloadUrl, fallbackDownloadUrl)}><RotateCcw size={14}/> {download.status === "cancelled" ? "Try again" : "Retry download"}</button>
      </div>
    );
  }

  return (
    <button className="sp-download-link-button sp-download-button" type="button" onClick={() => void start(resolvedDownloadUrl, fallbackDownloadUrl)}>
      <Download size={16}/> {label}
    </button>
  );
}
