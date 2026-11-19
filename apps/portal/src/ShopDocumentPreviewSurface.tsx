import { useEffect, useState } from "react";
import { FileText } from "lucide-react";
import { currentBrowserSupportsInlinePdfPreview, type PreviewMediaType } from "./document-preview";

interface Props {
  name: string;
  url: string;
  mediaType: string;
  printKind?: string | null;
}

/**
 * The single media surface used by the shop's document, ID and photo previews.
 * Every supported type shares the same frame; unsupported content never falls
 * back to a browser download/open action.
 */
export default function ShopDocumentPreviewSurface({ name, url, mediaType, printKind }: Props) {
  const [failed, setFailed] = useState(false);
  const [loading, setLoading] = useState(true);
  const isPdf = mediaType === "application/pdf";
  const isImage = mediaType === "image/jpeg" || mediaType === "image/png";
  const supportedType = (isPdf || isImage) ? mediaType as PreviewMediaType : null;
  const inlinePdfSupported = isPdf && currentBrowserSupportsInlinePdfPreview();
  const canRender = !failed && supportedType !== null && (!isPdf || inlinePdfSupported);
  const showPhotoWatermark = printKind === "PHOTO" && isPdf && canRender;
  const showImageWatermark = isImage && canRender;

  useEffect(() => {
    setFailed(false);
    setLoading(true);
  }, [url, mediaType]);

  const unavailableMessage = failed
    ? "We could not load this preview. Its access link may have expired or the shop connection may have been interrupted. Refresh the order or ask the shop to retry the preview."
    : isPdf && !inlinePdfSupported
      ? "This browser cannot display PDF files inline. Open the order in a browser with built-in PDF preview support."
      : "This file type is not supported for preview.";

  return (
    <div
      className="shop-document-preview"
      data-testid="shop-document-preview"
      data-preview-type={supportedType ?? "unsupported"}
      data-preview-state={failed ? "failed" : !supportedType || (isPdf && !inlinePdfSupported) ? "unsupported" : loading ? "loading" : "ready"}
      aria-label={name + " preview"}
      onContextMenu={event => {
        if (isImage || printKind === "PHOTO") event.preventDefault();
      }}
      onDragStart={event => {
        if (isImage || printKind === "PHOTO") event.preventDefault();
      }}
    >
      {canRender && isPdf && (
        <iframe
          src={url + "#toolbar=0&navpanes=0&scrollbar=0"}
          title={name + " PDF preview"}
          onLoad={() => setLoading(false)}
          onError={() => { setFailed(true); setLoading(false); }}
        />
      )}
      {canRender && isImage && (
        <img
          src={url}
          alt={name + " preview"}
          draggable={false}
          onLoad={() => setLoading(false)}
          onError={() => { setFailed(true); setLoading(false); }}
        />
      )}
      {canRender && loading && (
        <div className="shop-document-preview-loading" role="status" aria-live="polite">
          <span className="button-spinner" />
          <strong>Loading preview</strong>
          <small>Large documents may take a moment to appear.</small>
        </div>
      )}
      {!canRender && (
        <div className="shop-document-preview-unavailable" role="status">
          <FileText size={25} aria-hidden="true" />
          <strong>Preview unavailable</strong>
          <span>{unavailableMessage}</span>
        </div>
      )}
      {showPhotoWatermark && (
        <span className="shop-photo-preview-watermark" aria-hidden="true">
          SMARTPRINT · SHOP PRINT ONLY · PRIVATE PREVIEW
        </span>
      )}
      {showImageWatermark && (
        <span className="shop-photo-preview-watermark" aria-hidden="true">
          SMARTPRINT · PRIVATE SHOP PREVIEW
        </span>
      )}
    </div>
  );
}
