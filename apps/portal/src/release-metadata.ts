import { useCallback, useEffect, useState } from "react";

export type PublicRelease = {
  version: string | null;
  windowsAvailable: boolean;
  androidAvailable: boolean;
};

export type PublicReleaseState = "loading" | "ready" | "error";

export function getPublicDownloadPath(platform: "windows" | "android", version?: string | null): string {
  if (!version) return `/downloads/${platform}`;
  const assetName = platform === "windows" ? "SmartPrint-Windows-Setup.exe" : "SmartPrint-Android.apk";
  return `/downloads/${encodeURIComponent(version)}/${assetName}`;
}

function isPublicRelease(value: unknown): value is PublicRelease {
  if (typeof value !== "object" || value === null) return false;
  const candidate = value as Partial<PublicRelease>;
  return (candidate.version === null || typeof candidate.version === "string") &&
    typeof candidate.windowsAvailable === "boolean" &&
    typeof candidate.androidAvailable === "boolean";
}

export function usePublicRelease(): {
  release: PublicRelease | null;
  state: PublicReleaseState;
  refresh: () => Promise<void>;
} {
  const [release, setRelease] = useState<PublicRelease | null>(null);
  const [state, setState] = useState<PublicReleaseState>("loading");

  const refresh = useCallback(async (signal?: AbortSignal) => {
    setState("loading");
    try {
      const response = await fetch("/downloads/status", {
        headers: { Accept: "application/json" },
        cache: "no-store",
        signal
      });
      if (!response.ok) {
        throw new Error(`Download status lookup failed with HTTP ${response.status}.`);
      }

      const value: unknown = await response.json();
      if (!isPublicRelease(value)) {
        throw new Error("The download status response was not in the expected format.");
      }

      setRelease(value);
      setState("ready");
    } catch {
      if (!signal?.aborted) {
        setRelease(null);
        setState("error");
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    return () => controller.abort();
  }, [refresh]);

  return { release, state, refresh: () => refresh() };
}
