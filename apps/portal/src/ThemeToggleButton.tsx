import { Moon, Sun } from "lucide-react";
import { translateUiText, useLocale } from "./i18n";

export default function ThemeToggleButton({
  dark,
  onToggle,
  className = ""
}: {
  dark: boolean;
  onToggle: () => void;
  className?: string;
}) {
  const { locale } = useLocale();
  const label = translateUiText(locale, dark ? "Switch to light theme" : "Switch to dark theme");
  return (
    <button
      type="button"
      className={`theme-toggle-button ${className}`.trim()}
      onClick={onToggle}
      aria-label={label}
      title={label}
      aria-pressed={dark}
    >
      {dark ? <Sun size={17}/> : <Moon size={17}/>}
      <span>{translateUiText(locale, dark ? "Light mode" : "Dark mode")}</span>
    </button>
  );
}
