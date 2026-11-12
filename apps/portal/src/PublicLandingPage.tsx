import {
  ArrowRight, ArrowUpRight, Check, ChevronRight, Cloud, Download, FileCheck2, FileText,
  LockKeyhole, Monitor, Printer, QrCode, RotateCcw, ShieldCheck, Smartphone, Store, Wifi, X
} from "lucide-react";
import { getPublicDownloadPath, usePublicRelease } from "./release-metadata";
import { useFileDownload } from "./download-manager";
import ThemeToggleButton from "./ThemeToggleButton";
import BrandLockup from "./BrandLockup";
import "./public-landing.css";

export default function PublicLandingPage({ dark, onToggleTheme }: { dark: boolean; onToggleTheme: () => void }) {
  const { release, state: releaseState, refresh } = usePublicRelease();
  const windowsDownloadUrl = release?.windowsAvailable ? getPublicDownloadPath("windows") : null;
  const androidDownloadUrl = release?.androidAvailable ? getPublicDownloadPath("android") : null;

  return (
    <div className="sp-public">
      <header className="sp-public-header">
        <a className="sp-public-brand" href="/" aria-label="SmartPrint home">
          <BrandLockup className="sp-public-brand-lockup" />
        </a>
        <nav className="sp-public-nav" aria-label="Main navigation">
          <a href="#how-it-works">How it works</a>
          <a href="#for-shops">For shops</a>
          <a href="#for-customers">For customers</a>
          <a href="#downloads">Downloads</a>
        </nav>
        <ThemeToggleButton dark={dark} onToggle={onToggleTheme} className="sp-theme-toggle"/>
        <a className="sp-shop-entry" href="/shop">
          <span className="sp-shop-entry-icon"><Store size={18}/><Printer size={10}/></span>
          <span>For print shops</span>
          <ArrowUpRight size={15}/>
        </a>
      </header>

      <main>
        <section className="sp-hero">
          <div className="sp-hero-copy">
            <div className="sp-eyebrow"><span className="sp-eyebrow-dot"/> PRINTING, CONNECTED</div>
            <h1>From their phone<br/>to <span>your printer.</span><br/>Without the back-and-forth.</h1>
            <p className="sp-hero-lede">SmartPrint brings local print shops and customers together. Scan the shop QR, choose copies and colour for each file, and send an organised request for the shop to review.</p>
            <div className="sp-hero-actions">
              <a className="sp-button-primary" href="/shop?mode=register">Bring your shop online <ArrowRight size={17}/></a>
              <a className="sp-button-quiet" href="#how-it-works">See how it works <ChevronRight size={17}/></a>
            </div>
            <div className="sp-hero-trust"><span><ShieldCheck size={15}/> Shop-controlled print approval</span><span><LockKeyhole size={14}/> Secure job handoff</span></div>
          </div>

          <div className="sp-hero-art" aria-label="Illustration of a customer phone connecting to a print shop">
            <div className="sp-art-orbit sp-art-orbit-one"/>
            <div className="sp-art-orbit sp-art-orbit-two"/>
            <div className="sp-art-customer">
              <div className="sp-art-phone-top"><span/><i/></div>
              <div className="sp-art-phone-screen">
                <div className="sp-art-phone-brand"><span><Printer size={11}/></span> smartprint</div>
                <div className="sp-art-document"><FileText size={23}/><div><b>Project-notes.pdf</b><small>12 pages · PDF</small></div><Check size={14}/></div>
                <div className="sp-art-upload"><span/><span/><span/></div>
                <div className="sp-art-phone-button">Ready to print <ArrowRight size={11}/></div>
              </div>
              <div className="sp-art-phone-caption"><Smartphone size={12}/> CUSTOMER</div>
            </div>
            <div className="sp-art-connection"><span/><span/><span/><small>SECURE CONNECTION</small></div>
            <div className="sp-art-shop">
              <div className="sp-art-shop-head"><span className="sp-art-shop-icon"><Store size={16}/></span><div><b>Your print shop</b><small>Workspace connected</small></div><i/></div>
              <div className="sp-art-printer"><span><Printer size={25}/></span><div><b>Shop printer</b><small>Ready for the next job</small></div><em><Check size={12}/></em></div>
              <div className="sp-art-shop-foot"><span><Wifi size={12}/> Agent online</span><span>Job received</span></div>
              <div className="sp-art-shop-caption">YOUR SHOP</div>
            </div>
            <div className="sp-art-floating sp-art-floating-one"><QrCode size={16}/><span>Scan to start</span></div>
            <div className="sp-art-floating sp-art-floating-two"><Check size={15}/><span>Shop approved</span></div>
          </div>
        </section>

        <section className="sp-proof-strip" aria-label="SmartPrint workflow highlights">
          <div><QrCode/><span><b>One QR code</b><small>Connect each counter</small></span></div>
          <div><FileCheck2/><span><b>Clear job review</b><small>See each print request</small></span></div>
          <div><ShieldCheck/><span><b>Shop stays in control</b><small>Approve before printing</small></span></div>
          <div><Printer/><span><b>Your printers</b><small>Use the shop's setup</small></span></div>
        </section>

        <section className="sp-section sp-how" id="how-it-works">
          <div className="sp-section-heading">
            <div className="sp-eyebrow">A SIMPLE, CONNECTED FLOW</div>
            <h2>Three steps. One smoother print experience.</h2>
            <p>No scattered file messages. No repeating copy and colour instructions for every document.</p>
          </div>
          <div className="sp-steps">
            <article className="sp-step-card">
              <span className="sp-step-number">01</span><div className="sp-step-icon"><QrCode size={24}/></div>
              <h3>Scan &amp; send</h3><p>Customers scan your shop's QR code, add their documents and choose their print settings.</p>
              <span className="sp-step-tag">CUSTOMER</span>
            </article>
            <article className="sp-step-card">
              <span className="sp-step-number">02</span><div className="sp-step-icon"><FileCheck2 size={24}/></div>
              <h3>Review &amp; approve</h3><p>Your team reviews the document, checks the quote and payment, and authorises the job.</p>
              <span className="sp-step-tag">YOUR SHOP</span>
            </article>
            <article className="sp-step-card">
              <span className="sp-step-number">03</span><div className="sp-step-icon"><Printer size={24}/></div>
              <h3>Print &amp; hand over</h3><p>The connected Windows Agent routes approved jobs to the selected shop printer for output.</p>
              <span className="sp-step-tag">CONNECTED PRINTER</span>
            </article>
          </div>
        </section>

        <section className="sp-two-sided">
          <article className="sp-audience-card sp-customer-card" id="for-customers">
            <div className="sp-audience-kicker"><Smartphone size={15}/> FOR CUSTOMERS</div>
            <h2>Printing starts<br/>where you are.</h2>
            <p>Use your phone to connect to a nearby participating shop. Upload documents, review the print details and follow the job without passing files around.</p>
            <ul><li><Check/> Scan the shop's QR code</li><li><Check/> Upload supported documents</li><li><Check/> Review the quote and track the job</li></ul>
            <div className="sp-audience-visual sp-customer-visual"><div className="sp-customer-mini-phone"><div/><span><FileText size={24}/></span><b>Your document</b><small>Ready to submit</small></div><div className="sp-customer-mini-cloud"><Cloud size={34}/><span>Connected to shop</span></div></div>
            <a href="#downloads" className="sp-text-link">Explore the Android app <ArrowRight size={16}/></a>
          </article>
          <article className="sp-audience-card sp-shop-card" id="for-shops">
            <div className="sp-audience-kicker"><Store size={15}/> FOR PRINT SHOPS</div>
            <h2>Your counter.<br/>Your workflow.</h2>
            <p>Keep incoming print requests, pricing, printers and shop approvals in one workspace built around the way local print shops operate.</p>
            <ul><li><Check/> Manage QR stations and printers</li><li><Check/> Review jobs before authorising them</li><li><Check/> Keep control of shop payments and output</li></ul>
            <div className="sp-audience-visual sp-shop-visual"><div className="sp-shop-mini-window"><div className="sp-window-dots"><i/><i/><i/></div><div className="sp-mini-stat-row"><span/><span/><span/></div><div className="sp-mini-job"><FileText size={15}/><span/><em>IN REVIEW</em></div><div className="sp-mini-job"><Printer size={15}/><span/><em>APPROVED</em></div></div></div>
            <a href="/shop" className="sp-text-link">Open your shop workspace <ArrowRight size={16}/></a>
          </article>
        </section>

        <section className="sp-connection-section">
          <div className="sp-connection-copy">
            <div className="sp-eyebrow">BUILT AROUND YOUR SHOP</div>
            <h2>Your printers stay at your shop. Your workflow gets connected.</h2>
            <p>The SmartPrint Windows Agent connects the shop computer to the workspace. It discovers installed printer queues and receives only jobs authorised by the shop.</p>
            <div className="sp-connection-points"><span><Check/> Your existing Windows printer setup</span><span><Check/> Shop-controlled job authorisation</span><span><Check/> Local or cloud-assisted document transfer</span></div>
          </div>
          <div className="sp-connection-visual"><div className="sp-device-card"><Monitor size={29}/><b>Shop computer</b><small>SmartPrint Agent</small><span className="sp-online"><i/> Connected</span></div><div className="sp-connection-line"><span/><span/><span/></div><div className="sp-device-printer"><Printer size={35}/><b>Installed printer</b><small>Shop's printer queue</small></div><div className="sp-secure-pill"><LockKeyhole size={12}/> Authorised connection</div></div>
        </section>

        <section className="sp-download-section" id="downloads">
          <div className="sp-section-heading">
            <div className="sp-eyebrow">GET STARTED WITH SMARTPRINT</div>
            <h2>The right app for the right job.</h2>
            <p>Set up the shop computer or bring the customer experience to Android.</p>
          </div>
          <div className="sp-download-grid">
            <article className="sp-download-card">
              <div className="sp-download-icon sp-windows-icon"><Monitor size={25}/></div>
              <div className="sp-download-label-row">
                <div className="sp-download-platform">FOR SHOP OWNERS · WINDOWS 10/11</div>
                <span className={windowsDownloadUrl ? "sp-download-tag sp-download-tag-available" : "sp-download-tag sp-download-tag-coming"}>
                  {releaseState === "loading" ? "Checking" : releaseState === "error" ? "Temporarily unavailable" : windowsDownloadUrl ? "Available" : "Coming soon"}
                </span>
              </div>
              <h3>SmartPrint for Windows</h3><p>Install the shop Agent on the computer connected to your printers. The guided setup will help connect it to your shop workspace.</p>
              <DownloadAction platform="windows" label="Download Windows installer" available={Boolean(windowsDownloadUrl)} releaseState={releaseState} onRefresh={refresh}/>
              <small className="sp-download-note">Windows x64 · Guided installation · Official stable release</small>
              <p className="sp-download-security-note">The interim Windows beta installer is unsigned. Windows may show an Unknown Publisher or SmartScreen prompt; some managed devices may block unsigned installers.</p>
            </article>
            <article className="sp-download-card">
              <div className="sp-download-icon sp-android-icon"><Smartphone size={25}/></div>
              <div className="sp-download-label-row">
                <div className="sp-download-platform">FOR CUSTOMERS · ANDROID</div>
                <span className={androidDownloadUrl ? "sp-download-tag sp-download-tag-available" : "sp-download-tag sp-download-tag-coming"}>
                  {releaseState === "loading" ? "Checking" : releaseState === "error" ? "Temporarily unavailable" : androidDownloadUrl ? "Available" : "Coming soon"}
                </span>
              </div>
              <h3>SmartPrint for Android</h3><p>Scan shop QR codes, organise documents in your library and send files through the connected SmartPrint customer journey.</p>
              <DownloadAction platform="android" label="Download signed Android APK" available={Boolean(androidDownloadUrl)} releaseState={releaseState} onRefresh={refresh}/>
              <small className="sp-download-note">Android 8.0 or later · Production-signed APK</small>
              <p className="sp-release-pending">{androidDownloadUrl
                ? `Latest stable release: ${release?.version ?? "published"}.`
                : releaseState === "error"
                  ? "Download information is temporarily unavailable. Retry to check again."
                  : "The signed APK download appears automatically when the latest stable release contains the verified Android package."}</p>
            </article>
          </div>
          <p className="sp-download-footnote"><ShieldCheck size={14}/> Downloads are delivered securely from this SmartPrint website.</p>
        </section>

        <section className="sp-final-cta">
          <div><div className="sp-eyebrow">READY TO CONNECT YOUR SHOP?</div><h2>Give every print job<br/>a clearer way forward.</h2><p>Set up your shop workspace and bring your customers into the flow.</p></div>
          <a className="sp-button-light" href="/shop?mode=register">Create your shop account <ArrowRight size={17}/></a>
        </section>
      </main>
      <footer className="sp-public-footer">
        <a className="sp-public-brand" href="/"><BrandLockup className="sp-public-brand-lockup" /></a>
        <span>© {new Date().getFullYear()} SmartPrint by QuantaEdge</span>
        <div><a href="#how-it-works">How it works</a><a href="#downloads">Downloads</a><a href="/shop">For print shops</a></div>
      <nav className="sp-public-footer-resources" aria-label="Print shop resources">
          <span>PRINT SHOP RESOURCES</span>
          <a href="/features/">All features</a>
          <a href="/whatsapp-printing-alternative/">Chat-based print orders</a>
          <a href="/document-privacy-private-printing/">Document privacy</a>
          <a href="/photo-id-printing/">Photo and ID printing</a>
          <a href="/print-shop-software/">Print shop software</a>
          <a href="/qr-code-print-ordering/">QR code ordering</a>
          <a href="/print-job-management/">Print job management</a>
          <a href="/windows-print-agent/">Windows Print Agent</a>
          <a href="/smartprint-faq/">SmartPrint FAQ</a>
          <a href="/qr-printing-for-xerox-shops/">QR printing for Xerox shops</a>
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

function DownloadAction({
  platform,
  label,
  available,
  releaseState,
  onRefresh
}: {
  platform: DownloadPlatform;
  label: string;
  available: boolean;
  releaseState: "loading" | "ready" | "error";
  onRefresh: () => Promise<void>;
}) {
  const fileName = platform === "windows" ? "SmartPrint-Windows-Setup.exe" : "SmartPrint-Android.apk";
  const { download, start, cancel } = useFileDownload(fileName);
  const percent = download.totalBytes && download.totalBytes > 0
    ? Math.min(100, Math.floor((download.bytesReceived / download.totalBytes) * 100))
    : null;

  if (!available) {
    if (releaseState === "error") {
      return <button className="sp-download-unavailable sp-download-button" type="button" onClick={() => void onRefresh()}>Retry download availability</button>;
    }
    return <span className="sp-download-unavailable sp-download-button" aria-disabled="true">{releaseState === "loading" ? "Checking download availability…" : platform === "windows" ? "Windows installer not published" : "Signed APK not published"}</span>;
  }

  if (download.status === "downloading") {
    return (
      <div className="sp-download-progress" role="group" aria-label={`${label} progress`}>
        <div className="sp-download-progress-heading">
          <span><span className="sp-download-live-dot"/> Downloading</span>
          <strong>{percent === null ? "In progress" : `${percent}%`}</strong>
        </div>
        <div className="sp-download-progress-track" role="progressbar" aria-label={`${fileName} download progress`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent ?? undefined}>
          <span className={percent === null ? "sp-download-progress-fill sp-download-progress-indeterminate" : "sp-download-progress-fill"} style={percent === null ? undefined : { width: `${percent}%` }}/>
        </div>
        <div className="sp-download-progress-details">
          <span>{formatDownloadBytes(download.bytesReceived)} downloaded</span>
          <span>{download.totalBytes ? `of ${formatDownloadBytes(download.totalBytes)}` : "Total size unavailable"}</span>
        </div>
        <button className="sp-download-cancel" type="button" onClick={cancel}><X size={14}/> Cancel download</button>
      </div>
    );
  }

  if (download.status === "complete") {
    return (
      <div className="sp-download-result sp-download-result-success" role="status">
        <div><Check size={15}/><span>Download complete</span><strong>{formatDownloadBytes(download.bytesReceived)}</strong></div>
        <button type="button" onClick={() => void start(getPublicDownloadPath(platform))}><Download size={14}/> Download again</button>
      </div>
    );
  }

  if (download.status === "error" || download.status === "cancelled") {
    return (
      <div className={download.status === "error" ? "sp-download-result sp-download-result-error" : "sp-download-result"}>
        <p role="status">{download.status === "cancelled" ? "Download cancelled." : download.errorMessage ?? "The download could not be completed."}</p>
        <button type="button" onClick={() => void start(getPublicDownloadPath(platform))}><RotateCcw size={14}/> {download.status === "cancelled" ? "Try again" : "Retry download"}</button>
      </div>
    );
  }

  return (
    <button className="sp-download-link-button sp-download-button" type="button" onClick={() => void start(getPublicDownloadPath(platform))}>
      <Download size={16}/> {label}
    </button>
  );
}
