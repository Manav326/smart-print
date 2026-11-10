import { useState, type FormEvent } from "react";
import { ArrowLeft, ArrowRight, Eye, EyeOff, LockKeyhole, Mail, ShieldCheck } from "lucide-react";
import ThemeToggleButton from "./ThemeToggleButton";
import BrandLockup from "./BrandLockup";
import {
  isPlatformAdminSession,
  signIn,
  storeTokens,
  type AuthTokens
} from "./api";

export default function PlatformAdminAuthScreen({
  onAuthenticated,
  dark,
  onToggleTheme
}: {
  onAuthenticated: (tokens: AuthTokens) => void;
  dark: boolean;
  onToggleTheme: () => void;
}) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    setBusy(true);
    try {
      const tokens = await signIn(email.trim(), password);
      if (!isPlatformAdminSession(tokens)) {
        throw new Error("This account is not authorized for platform administration. Use your shop sign-in at /shop.");
      }
      await storeTokens(tokens, "admin");
      onAuthenticated(tokens);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Unable to sign in to platform administration.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="auth-screen platform-admin-auth-screen">
      <div className="auth-glow auth-glow-one"/><div className="auth-glow auth-glow-two"/>
      <header className="auth-topbar">
        <BrandLockup className="auth-brand" subline="PLATFORM ADMINISTRATION" />
        <div className="auth-top-actions">
          <ThemeToggleButton dark={dark} onToggle={onToggleTheme}/>
          <a className="auth-home-link" href="/"><ArrowLeft size={14}/> Back to website</a>
          <span className="auth-top-note"><ShieldCheck size={15}/> Restricted platform access</span>
        </div>
      </header>
      <main className="auth-layout">
        <section className="auth-story">
          <div className="auth-kicker"><ShieldCheck size={14}/> PLATFORM CONTROL CENTRE</div>
          <h1>One platform.<br/><span>Clear control.</span></h1>
          <p>Manage shop tenants, plans, subscriptions and platform operations from a dedicated administrative workspace.</p>
          <div className="auth-benefits">
            <div><span><ShieldCheck size={17}/></span><div><strong>Separate platform scope</strong><small>Platform access is distinct from every shop workspace.</small></div></div>
            <div><span><LockKeyhole size={17}/></span><div><strong>Protected administration</strong><small>Only accounts with platform administrator privileges can continue.</small></div></div>
            <div><span><Mail size={17}/></span><div><strong>Individual accounts</strong><small>Use your own authorized administrator credentials.</small></div></div>
          </div>
          <div className="auth-proof"><span className="proof-line"/><span>SECURE PLATFORM ACCESS</span></div>
        </section>
        <section className="auth-card">
          <div className="auth-card-heading">
            <div className="auth-card-icon"><ShieldCheck size={20}/></div>
            <div><span>PLATFORM ADMINISTRATION</span><h2>Administrator sign in</h2><p>Sign in with your authorized platform account.</p></div>
          </div>
          <form className="auth-form" onSubmit={submit}>
            <label>Email address<div className="auth-input"><Mail size={16}/><input type="email" autoComplete="username" value={email} onChange={event => setEmail(event.target.value)} placeholder="admin@yourcompany.com" required maxLength={256}/></div></label>
            <label>Password<div className="auth-input"><LockKeyhole size={16}/><input type={showPassword ? "text" : "password"} autoComplete="current-password" value={password} onChange={event => setPassword(event.target.value)} placeholder="Your administrator password" required/><button type="button" className="password-eye" aria-label={showPassword ? "Hide password" : "Show password"} onClick={() => setShowPassword(value => !value)}>{showPassword ? <EyeOff size={16}/> : <Eye size={16}/>}</button></div></label>
            {error && <div className="auth-error" role="alert">{error}</div>}
            <button className="auth-submit" type="submit" disabled={busy}>{busy ? <span className="button-spinner"/> : <>Sign in to administration <ArrowRight size={16}/></>}</button>
          </form>
          <div className="auth-secure-note"><LockKeyhole size={13}/> Shop-owner accounts cannot access this workspace.</div>
          <div className="auth-card-footer">Access is authenticated and authorized by the QRtoPrint platform API.</div>
        </section>
      </main>
      <footer className="auth-footer"><span>© 2026 Smart Print by QRtoPrint</span><span>Platform administration</span><span>Authorized personnel only.</span></footer>
    </div>
  );
}
