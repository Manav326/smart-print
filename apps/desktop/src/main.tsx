import React from "react";
import ReactDOM from "react-dom/client";
import App from "../../portal/src/App";
import { readDesktopSession, isDesktopApp } from "../../portal/src/desktop-bridge";
import { storeTokens } from "../../portal/src/api";
import "../../portal/src/styles.css";
import "./desktop.css";

async function start() {
  document.body.dataset.appShell = "desktop";
  if (!isDesktopApp()) {
    document.body.innerHTML = "<main style='font:16px sans-serif;padding:32px'>Smart Print must run inside its Windows application.</main>";
    return;
  }

  try {
    const saved = await readDesktopSession();
    if (saved) {
      const parsed = JSON.parse(saved);
      if (parsed && typeof parsed.accessToken === "string" && typeof parsed.refreshToken === "string") {
        await storeTokens(parsed);
      }
    }
  } catch {
    // A missing or invalid secure session simply returns the owner to sign-in.
    // Credential Manager itself can be unavailable; that must not strand the
    // owner on a blank window before React has a chance to show the sign-in UI.
    try {
      await storeTokens(null);
    } catch {
      // The next sign-in attempt will surface a secure-session write failure.
    }
  }

  if (window.location.pathname !== "/shop") {
    window.history.replaceState(null, "", "/shop");
  }

  const root = document.getElementById("smartprint-desktop-root");
  if (!root) {
    throw new Error("Smart Print could not find #smartprint-desktop-root.");
  }

  ReactDOM.createRoot(root).render(
    <React.StrictMode><App /></React.StrictMode>
  );
}

void start();