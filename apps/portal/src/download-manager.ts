import { useCallback, useEffect, useRef, useState } from "react";

export type FileDownloadState = {
  status: "idle" | "downloading" | "complete" | "cancelled" | "error";
  bytesReceived: number;
  totalBytes: number | null;
  bytesPerSecond: number;
  errorMessage: string | null;
};

const initialState: FileDownloadState = {
  status: "idle",
  bytesReceived: 0,
  totalBytes: null,
  bytesPerSecond: 0,
  errorMessage: null
};

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

function getDownloadError(error: unknown): string {
  if (error instanceof Error && error.message) return error.message;
  return "The download could not be completed. Please try again.";
}

function saveBlob(blob: Blob, fileName: string): void {
  const objectUrl = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = objectUrl;
  anchor.download = fileName;
  anchor.style.display = "none";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  window.setTimeout(() => URL.revokeObjectURL(objectUrl), 60_000);
}

/**
 * Downloads an immutable public release asset from the R2 custom domain when
 * configured, with the same-origin API route as a transparent fallback. Measures
 * actual response bytes and only hands a complete, size-checked Blob to the
 * browser, preserving the existing save-after-completion experience.
 */
export function useFileDownload(fileName: string): {
  download: FileDownloadState;
  start: (url: string, fallbackUrl?: string | null) => Promise<void>;
  cancel: () => void;
} {
  const [download, setDownload] = useState<FileDownloadState>(initialState);
  const controllerRef = useRef<AbortController | null>(null);

  const start = useCallback(async (url: string, fallbackUrl?: string | null) => {
    if (controllerRef.current) return;

    const controller = new AbortController();
    controllerRef.current = controller;

    setDownload({
      status: "downloading",
      bytesReceived: 0,
      totalBytes: null,
      bytesPerSecond: 0,
      errorMessage: null
    });

    try {
      const requestOptions: RequestInit = {
        method: "GET",
        headers: { Accept: "application/octet-stream" },
        // The CDN object is immutable and public. Allow the browser HTTP cache
        // to accelerate repeat downloads on the same device; the API fallback
        // remains private/no-store and is not retained by the browser.
        cache: "default",
        credentials: "omit",
        signal: controller.signal
      };

      let response: Response;
      try {
        response = await fetch(url, requestOptions);
      } catch (primaryError) {
        if (!fallbackUrl || fallbackUrl === url || controller.signal.aborted) {
          throw primaryError;
        }
        // CORS/network failures on the R2 custom domain transparently fall back
        // to the established same-origin API route.
        response = await fetch(fallbackUrl, requestOptions);
      }

      if (!response.ok && fallbackUrl && fallbackUrl !== url) {
        // Also fall back when R2 has not received the immutable object yet.
        await response.body?.cancel();
        response = await fetch(fallbackUrl, requestOptions);
      }
      if (!response.ok) {
        if (response.status === 404) {
          throw new Error("This release file is no longer available. Refresh the page and try again.");
        }
        throw new Error(`The download server returned HTTP ${response.status}. Please retry.`);
      }

      const contentLength = Number(response.headers.get("content-length"));
      const totalBytes = Number.isFinite(contentLength) && contentLength > 0 ? contentLength : null;
      let bytesReceived = 0;
      const chunks: BlobPart[] = [];
      const reader = response.body?.getReader();
      const receiveStartedAt = performance.now();
      let lastProgressUpdate = receiveStartedAt;
      let lastProgressBytes = 0;
      let smoothedBytesPerSecond = 0;

      if (reader) {
        while (true) {
          const result = await reader.read();
          if (result.done) break;
          const chunk = result.value;
          bytesReceived += chunk.byteLength;
          // Retain the immutable stream chunk view. Blob snapshots each view once
          // at final assembly; slice().buffer here would make an extra full chunk copy.
          chunks.push(chunk as unknown as BlobPart);

          const now = performance.now();
          const elapsedSeconds = (now - lastProgressUpdate) / 1000;
          if (elapsedSeconds >= 0.2) {
            const intervalBytesPerSecond = (bytesReceived - lastProgressBytes) / elapsedSeconds;
            // EWMA smooths bursty mobile/Wi-Fi packet delivery without hiding
            // sustained slowdowns from the customer.
            smoothedBytesPerSecond = smoothedBytesPerSecond > 0
              ? (smoothedBytesPerSecond * 0.65) + (intervalBytesPerSecond * 0.35)
              : intervalBytesPerSecond;

            setDownload({
              status: "downloading",
              bytesReceived,
              totalBytes,
              bytesPerSecond: smoothedBytesPerSecond,
              errorMessage: null
            });
            lastProgressUpdate = now;
            lastProgressBytes = bytesReceived;
          }
        }
      } else {
        const blob = await response.blob();
        bytesReceived = blob.size;
        chunks.push(blob);

        setDownload({
          status: "downloading",
          bytesReceived,
          totalBytes: totalBytes ?? bytesReceived,
          bytesPerSecond: smoothedBytesPerSecond,
          errorMessage: null
        });
      }

      // For very small files and fast transfers, the stream may finish before the
      // first 200 ms sample. Report the real average instead of showing 0 B/s.
      const totalReceiveSeconds = (performance.now() - receiveStartedAt) / 1000;
      if (smoothedBytesPerSecond <= 0 && totalReceiveSeconds > 0) {
        smoothedBytesPerSecond = bytesReceived / totalReceiveSeconds;
      }

      // Publish the exact final byte count even when the last read arrived inside
      // the progress-throttle window.
      setDownload({
        status: "downloading",
        bytesReceived,
        totalBytes: totalBytes ?? bytesReceived,
        bytesPerSecond: smoothedBytesPerSecond,
        errorMessage: null
      });

      if (bytesReceived <= 0) {
        throw new Error("The download server returned an empty file. Please retry.");
      }

      if (totalBytes !== null && bytesReceived !== totalBytes) {
        throw new Error(`The download was incomplete (${bytesReceived} of ${totalBytes} bytes received). Please retry.`);
      }

      const blob = new Blob(chunks, {
        type: response.headers.get("content-type") || "application/octet-stream"
      });
      if (blob.size !== bytesReceived) {
        throw new Error("The downloaded file could not be assembled correctly. Please retry.");
      }

      // Let the browser handle the actual save. No File System Access API or
      // persistent write permission is requested from the customer.
      saveBlob(blob, fileName);

      setDownload({
        status: "complete",
        bytesReceived,
        totalBytes: totalBytes ?? bytesReceived,
        bytesPerSecond: smoothedBytesPerSecond,
        errorMessage: null
      });
    } catch (error) {
      if (isAbortError(error) || controller.signal.aborted) {
        setDownload(current => ({
          ...current,
          status: "cancelled",
          errorMessage: null
        }));
      } else {
        setDownload(current => ({
          ...current,
          status: "error",
          errorMessage: getDownloadError(error)
        }));
      }
    } finally {
      if (controllerRef.current === controller) controllerRef.current = null;
    }
  }, [fileName]);

  const cancel = useCallback(() => {
    controllerRef.current?.abort();
  }, []);

  useEffect(() => () => controllerRef.current?.abort(), []);

  return { download, start, cancel };
}
