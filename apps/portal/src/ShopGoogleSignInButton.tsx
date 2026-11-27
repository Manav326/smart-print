import { useCallback, useEffect, useRef, useState } from "react";
import {
  getGoogleAuthConfiguration,
  signInShopWithGoogle,
  getGoogleAuthChallenge,
  type AuthTokens
} from "./api";
import { translateUiText, useLocale } from "./i18n";

type GoogleCredentialResponse = { credential?: string };
type GoogleIdentity = {
  initialize: (options: {
    client_id: string;
    callback: (response: GoogleCredentialResponse) => void;
    auto_select?: boolean;
    cancel_on_tap_outside?: boolean;
    context?: "signin";
    nonce?: string;
    error_callback?: (error: { type?: string }) => void;
  }) => void;
  renderButton: (target: HTMLElement, options: Record<string, string | number | boolean>) => void;
};

declare global {
  interface Window {
    google?: { accounts?: { id?: GoogleIdentity } };
  }
}

let googleScriptPromise: Promise<void> | null = null;

function loadGoogleIdentityServices(): Promise<void> {
  if (window.google?.accounts?.id) return Promise.resolve();
  if (!googleScriptPromise) {
    const pending = new Promise<void>((resolve, reject) => {
      let script = document.querySelector<HTMLScriptElement>('script[data-smartprint-google-identity="true"]');
      if (script?.dataset.failed === "true") {
        script.remove();
        script = null;
      }

      const loaded = () => {
        if (script) script.dataset.loaded = "true";
        if (window.google?.accounts?.id) resolve();
        else reject(new Error("Google sign-in did not initialize. Please reload and try again."));
      };
      const failed = () => {
        if (script) {
          script.dataset.failed = "true";
          script.remove();
        }
        reject(new Error("Google sign-in could not be loaded. Check your connection and try again."));
      };

      if (script) {
        if (script.dataset.loaded === "true") {
          reject(new Error("Google sign-in did not initialize. Please reload and try again."));
          return;
        }
        script.addEventListener("load", loaded, { once: true });
        script.addEventListener("error", failed, { once: true });
        return;
      }

      script = document.createElement("script");
      script.src = "https://accounts.google.com/gsi/client";
      script.async = true;
      script.defer = true;
      script.dataset.smartprintGoogleIdentity = "true";
      script.addEventListener("load", loaded, { once: true });
      script.addEventListener("error", failed, { once: true });
      document.head.appendChild(script);
    });
    googleScriptPromise = pending.catch(error => {
      googleScriptPromise = null;
      throw error;
    });
  }
  return googleScriptPromise;
}

export default function ShopGoogleSignInButton({
  onAuthenticated
}: {
  onAuthenticated: (tokens: AuthTokens) => Promise<void> | void;
}) {
  const { locale } = useLocale();
  const localeRef = useRef(locale);
  useEffect(() => { localeRef.current = locale; }, [locale]);
  const t = useCallback((source: string) => translateUiText(localeRef.current, source), []);
  const googleLocale = locale === "hindi" ? "hi" : "en";
  const target = useRef<HTMLDivElement>(null);
  const [ready, setReady] = useState(false);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [unavailable, setUnavailable] = useState(false);
  const [error, setError] = useState("");
  const [challengeRevision, setChallengeRevision] = useState(0);

  const receiveCredential = useCallback(async (response: GoogleCredentialResponse, nonce: string) => {
    const credential = String(response.credential ?? "").trim();
    if (!credential) {
      setError(t("Google did not return a sign-in credential. Please try again."));
      return;
    }

    setBusy(true);
    setError("");
    try {
      const tokens = await signInShopWithGoogle(credential, nonce);
      await onAuthenticated(tokens);
    } catch (reason) {
      setError(reason instanceof Error ? t(reason.message) : t("Google sign-in could not be completed."));
    } finally {
      setBusy(false);
    }
  }, [onAuthenticated, t]);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError("");

    void (async () => {
      const config = await getGoogleAuthConfiguration();
      if (!config.configured || !config.clientId?.trim()) {
        if (active) {
          setUnavailable(true);
          setLoading(false);
        }
        return;
      }

      await loadGoogleIdentityServices();
      if (!active || !target.current || !window.google?.accounts?.id) return;
      const challenge = await getGoogleAuthChallenge("shop");
      if (!active || !target.current) return;

      window.google.accounts.id.initialize({
        client_id: config.clientId.trim(),
        callback: result => { void receiveCredential(result, challenge.nonce); },
        auto_select: false,
        cancel_on_tap_outside: true,
        context: "signin",
        nonce: challenge.nonce,
        error_callback: () => setError(t("Google sign-in was cancelled or could not be started."))
      });
      setReady(true);
      setLoading(false);
    })().catch(reason => {
      if (active) {
        setError(reason instanceof Error ? t(reason.message) : t("Google sign-in could not be loaded."));
        setLoading(false);
      }
    });

    return () => { active = false; };
  }, [receiveCredential, challengeRevision, t]);

  // Google supports a locale option on renderButton. Re-render only its button
  // on language changes; initialize the sign-in client once per challenge.
  useEffect(() => {
    if (!ready || !target.current || !window.google?.accounts?.id) return;
    const container = target.current;
    container.replaceChildren();
    window.google.accounts.id.renderButton(container, {
      type: "standard",
      theme: "outline",
      size: "large",
      text: "continue_with",
      shape: "pill",
      logo_alignment: "left",
      width: Math.min(350, Math.max(240, container.clientWidth || 320)),
      locale: googleLocale
    });
  }, [ready, googleLocale]);

  return (
    <div className="auth-google-signin">
      <div className="auth-google-divider"><span>{t("OR")}</span></div>
      <div ref={target} className="auth-google-signin__target" aria-label={t("Continue with Google")} />
      {busy && <p className="auth-google-signin__status" role="status">{t("Verifying your Google account securely…")}</p>}
      {!ready && loading && !error && <p className="auth-google-signin__status" role="status">{t("Preparing Google sign-in…")}</p>}
      {unavailable && <p className="auth-google-signin__status" role="status">{t("Google sign-in is not configured for this environment yet.")}</p>}
      {error && (
        <div className="auth-google-signin__retry">
          <p className="auth-google-signin__error" role="alert">{error}</p>
          <button
            type="button"
            className="auth-google-signin__retry-button"
            onClick={() => {
              setError("");
              setUnavailable(false);
              setReady(false);
              setLoading(true);
              setChallengeRevision(value => value + 1);
            }}
          >
            {t("Try Google sign-in again")}
          </button>
        </div>
      )}
    </div>
  );
}
