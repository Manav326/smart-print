import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./styles.css";
import { LanguageProvider, LanguageSwitcher } from "./i18n";

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <LanguageProvider>
      <App />
      <LanguageSwitcher />
    </LanguageProvider>
  </React.StrictMode>
);
