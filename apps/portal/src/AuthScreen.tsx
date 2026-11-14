import { useCallback, useState, type FormEvent } from "react";
import { ArrowLeft, ArrowRight, Eye, EyeOff, LockKeyhole, Mail, Printer, ShieldCheck, Sparkles, Store, UserRound } from "lucide-react";
import { isPlatformAdminSession, registerShop, signIn, storeTokens, type AuthTokens } from "./api";
import { isDesktopApp } from "./desktop-bridge";
import ThemeToggleButton from "./ThemeToggleButton";
import BrandLockup from "./BrandLockup";
import ShopGoogleSignInButton from "./ShopGoogleSignInButton";

export default function AuthScreen({ onAuthenticated, dark, onToggleTheme }: { onAuthenticated: (tokens: AuthTokens) => void; dark: boolean; onToggleTheme: () => void }) {
  const desktop = isDesktopApp();
  const initialMode = window.location.pathname.replace(/\/+$/, "") === "/signup" || new URLSearchParams(window.location.search).get("mode") === "register" ? "register" : "signin";
  const [mode, setMode] = useState<"signin" | "register">(initialMode);
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [businessName, setBusinessName] = useState("");
  const [ownerName, setOwnerName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");

  const authenticateWithGoogle = useCallback(async (tokens: AuthTokens) => {
    if (isPlatformAdminSession(tokens)) {
      throw new Error("Platform administrator accounts must sign in at /admin.");
    }
    await storeTokens(tokens, "shop");
    onAuthenticated(tokens);
  }, [onAuthenticated]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError("");
    if (mode === "register" && password.length < 12) {
      setError("Use at least 12 characters for your password.");
      return;
    }

    setBusy(true);
    try {
      const tokens = mode === "register"
        ? await registerShop({ businessName, ownerName, email, password })
        : await signIn(email, password);
      if (isPlatformAdminSession(tokens)) {
        throw new Error("Platform administrator accounts must sign in at /admin.");
      }
      // On desktop, persist the shop session in Windows Credential Manager
      // before entering the workspace. Web shop credentials have their own key.
      await storeTokens(tokens, "shop");
      onAuthenticated(tokens);
    } catch (exception) {
      setError(exception instanceof Error ? exception.message : "Something went wrong. Please try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="auth-screen">
      <div className="auth-glow auth-glow-one"/><div className="auth-glow auth-glow-two"/>
      <header className="auth-topbar">
        <BrandLockup className="auth-brand" />
        <div className="auth-top-actions"><ThemeToggleButton dark={dark} onToggle={onToggleTheme}/>{!desktop && <a className="auth-home-link" href="/"><ArrowLeft size={14}/> Back to website</a>}<span className="auth-top-note"><ShieldCheck size={15}/> Secure Smart Print workspace</span></div>
      </header>
      <main className="auth-layout">
        <section className="auth-story">
          <div className="auth-kicker"><Sparkles size={14}/> PRINTING, REIMAGINED</div>
          <h1>Your print shop.<br/><span>In perfect flow.</span></h1>
          <p>One beautifully simple workspace to manage printers, connect customers and keep every job moving.</p>
          <div className="auth-benefits">
            <div><span><QrCodeIcon/></span><div><strong>One scan to start</strong><small>Let customers connect through a secure QR station.</small></div></div>
            <div><span><Printer size={17}/></span><div><strong>Your printers, together</strong><small>Bring different printer brands into one workspace.</small></div></div>
            <div><span><LockKeyhole size={17}/></span><div><strong>Your shop stays yours</strong><small>Private data and settings, isolated to your shop.</small></div></div>
          </div>
          <div className="auth-proof"><span className="proof-line"/><span>BUILT FOR INDEPENDENT PRINT SHOPS</span></div>
        </section>
        <section className="auth-card">
          <div className="auth-card-heading"><div className="auth-card-icon"><Store size={20}/></div><div><span>WELCOME TO SMART PRINT</span><h2>{mode === "signin" ? "Welcome back" : "Set up your shop"}</h2><p>{mode === "signin" ? "Sign in to pick up where you left off." : "Create your shop workspace in a few steps."}</p></div></div>
          <div className="auth-tabs"><button className={mode === "signin" ? "selected" : ""} onClick={() => {setMode("signin");setError("");}}>Sign in</button><button className={mode === "register" ? "selected" : ""} onClick={() => {setMode("register");setError("");}}>Create shop</button></div>
          <form className="auth-form" onSubmit={submit}>
            {mode === "register" && <>
              <label>Shop or business name<div className="auth-input"><Store size={16}/><input value={businessName} onChange={e=>setBusinessName(e.target.value)} placeholder="e.g. City Print House" required maxLength={180}/></div></label>
              <label>Your name<div className="auth-input"><UserRound size={16}/><input value={ownerName} onChange={e=>setOwnerName(e.target.value)} placeholder="Shop owner name" required maxLength={160}/></div></label>
            </>}
            <label>Email address<div className="auth-input"><Mail size={16}/><input type="email" autoComplete="email" value={email} onChange={e=>setEmail(e.target.value)} placeholder="you@yourshop.com" required maxLength={256}/></div></label>
            <label>Password<div className="auth-input"><LockKeyhole size={16}/><input type={showPassword ? "text" : "password"} autoComplete={mode === "signin" ? "current-password" : "new-password"} value={password} onChange={e=>setPassword(e.target.value)} placeholder={mode === "signin" ? "Your password" : "At least 12 characters"} required minLength={mode === "register" ? 12 : 1}/><button type="button" className="password-eye" aria-label={showPassword ? "Hide password" : "Show password"} onClick={()=>setShowPassword(!showPassword)}>{showPassword ? <EyeOff size={16}/> : <Eye size={16}/>}</button></div></label>
            {error && <div className="auth-error" role="alert">{error}</div>}
            <button className="auth-submit" type="submit" disabled={busy}>{busy ? <span className="button-spinner"/> : <>{mode === "signin" ? "Sign in to workspace" : "Create my shop"}<ArrowRight size={16}/></>}</button>
          </form>
          {mode === "signin" && <ShopGoogleSignInButton onAuthenticated={authenticateWithGoogle} />}
          {mode === "register" ? <div className="trial-note"><ShieldCheck size={15}/> Your shop starts with a 14-day trial. No payment is collected during registration.</div> : <div className="auth-secure-note"><LockKeyhole size={13}/> Your connection is protected with encrypted authentication.</div>}
          <div className="auth-card-footer">By continuing, you agree to the Smart Print service terms and privacy policy.</div>
        </section>
      </main>
      <footer className="auth-footer"><span>© 2026 SmartPrint by QuantaEdge</span><span>thinkwithsujeet.in</span><span>Thoughtfully made for print shops.</span></footer>
    </div>
  );
}

function QrCodeIcon() {
  return <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><path d="M14 14h3v3h-3zM20 14v2M14 20h2M20 19v2M18 17h3"/></svg>;
}
