import { useEffect, useState } from "react";
import PrivatePhotoStudioEntry from "./PrivatePhotoStudioEntry";
import { applyThemePreference, readDarkModePreference } from "./ui-preferences";

export default function PhotoStudioOfflineApp() {
  const [dark] = useState(readDarkModePreference);

  useEffect(() => {
    applyThemePreference(dark);
  }, [dark]);

  return <PrivatePhotoStudioEntry dark={dark} />;
}
