import { useCallback, useEffect, useRef, useState } from "react";
import {
  getGoogleAuthConfiguration,
  signInShopWithGoogle,
  getGoogleAuthChallenge,
  type AuthTokens
} from "./api";

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
      setError("Google did not return a sign-in credential. Please try again.");
      return;
    }

    setBusy(true);
    setError("");
    try {
      const tokens = await signInShopWithGoogle(credential, nonce);
      await onAuthenticated(tokens);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Google sign-in could not be completed.");
    } finally {
      setBusy(false);
    }
  }, [onAuthenticated]);

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

      target.current.replaceChildren();
      window.google.accounts.id.initialize({
        client_id: config.clientId.trim(),
        callback: result => { void receiveCredential(result, challenge.nonce); },
        auto_select: false,
        cancel_on_tap_outside: true,
        context: "signin",
        nonce: challenge.nonce,
        error_callback: () => setError("Google sign-in was cancelled or could not be started.")
      });
      window.google.accounts.id.renderButton(target.current, {
        type: "standard",
        theme: "outline",
        size: "large",
        text: "continue_with",
        shape: "pill",
        logo_alignment: "left",
        width: Math.min(350, Math.max(240, target.current.clientWidth || 320)),
        locale: "en"
      });
      setReady(true);
      setLoading(false);
    })().catch(reason => {
      if (active) {
        setError(reason instanceof Error ? reason.message : "Google sign-in could not be loaded.");
        setLoading(false);
      }
    });

    return () => { active = false; };
  }, [receiveCredential, challengeRevision]);

  return (
    <div className="auth-google-signin">
      <div className="auth-google-divider"><span>OR</span></div>
      <div ref={target} className="auth-google-signin__target" aria-label="Continue with Google" />
      {busy && <p className="auth-google-signin__status" role="status">Verifying your Google account securely…</p>}
      {!ready && loading && !error && <p className="auth-google-signin__status" role="status">Preparing Google sign-in…</p>}
      {unavailable && <p className="auth-google-signin__status" role="status">Google sign-in is not configured for this environment yet.</p>}
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
            Try Google sign-in again
          </button>
        </div>
      )}
    </div>
  );
}
