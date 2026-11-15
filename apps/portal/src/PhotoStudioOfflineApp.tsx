import { useEffect, useState } from "react";
import PrivatePhotoStudioEntry from "./PrivatePhotoStudioEntry";
import { applyThemePreference, readDarkModePreference } from "./ui-preferences";

export default function PhotoStudioOfflineApp() {
  const [dark, setDark] = useState(readDarkModePreference);

  useEffect(() => {
    applyThemePreference(dark);
  }, [dark]);

  useEffect(() => {
    const syncNativeTheme = (event: Event) => {
      const selected = (event as CustomEvent<string>).detail;
      if (selected === "light" || selected === "dark") setDark(selected === "dark");
    };
    window.addEventListener("smartprint-theme-change", syncNativeTheme);
    return () => window.removeEventListener("smartprint-theme-change", syncNativeTheme);
  }, []);

  return <PrivatePhotoStudioEntry dark={dark} />;
}
