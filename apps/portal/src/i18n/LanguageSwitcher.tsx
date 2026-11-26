import { Languages } from "lucide-react";
import { UI_LOCALE_OPTIONS, translateUi } from "./catalog";
import { useLocale } from "./LanguageProvider";
import "./language-switcher.css";

export default function LanguageSwitcher({ compact = false }: { compact?: boolean }) {
  const { locale, setLocale } = useLocale();
  return (
    <label className={"sp-language-switcher" + (compact ? " sp-language-switcher--compact" : "")}>
      <Languages size={15} aria-hidden="true" />
      <span className="sp-language-switcher__sr">{translateUi(locale, "common.language")}</span>
      <select aria-label={translateUi(locale, "common.language")} value={locale} onChange={event => {
        const selected = UI_LOCALE_OPTIONS.find(option => option.id === event.currentTarget.value);
        if (selected) setLocale(selected.id);
      }}>
        {UI_LOCALE_OPTIONS.map(option => <option key={option.id} value={option.id}>{option.nativeLabel}</option>)}
      </select>
    </label>
  );
}
