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
  const isPdf = mediaType === "application/pdf";
  const isImage = mediaType === "image/jpeg" || mediaType === "image/png";
  const supportedType = (isPdf || isImage) ? mediaType as PreviewMediaType : null;
  const inlinePdfSupported = isPdf && currentBrowserSupportsInlinePdfPreview();
  const canRender = !failed && supportedType !== null && (!isPdf || inlinePdfSupported);
  const showPhotoWatermark = printKind === "PHOTO" && isPdf && canRender;
  const showImageWatermark = isImage && canRender;

  useEffect(() => setFailed(false), [url, mediaType]);

  const unavailableMessage = failed
    ? "The file could not be loaded. Refresh the order and try again."
    : isPdf && !inlinePdfSupported
      ? "This browser cannot display PDF files inline."
      : "This file type is not supported for preview.";

  return (
    <div
      className="shop-document-preview"
      data-testid="shop-document-preview"
      data-preview-type={supportedType ?? "unsupported"}
      data-preview-state={canRender ? "available" : "unavailable"}
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
          onError={() => setFailed(true)}
        />
      )}
      {canRender && isImage && (
        <img
          src={url}
          alt={name + " preview"}
          draggable={false}
          onError={() => setFailed(true)}
        />
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
