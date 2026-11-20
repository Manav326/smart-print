import { createPortal } from "react-dom";
import {
  buildQrStationArtworkSvg,
  type QrStationArtworkSource
} from "./qr-station-export";

type Props = {
  artwork: QrStationArtworkSource | null;
};

function svgDataUri(svg: string): string {
  return "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svg);
}

export default function QrStationPrintDocument({ artwork }: Props) {
  if (!artwork) return null;

  // The print poster and PNG/JPG/share all use this same canonical artwork.
  const posterSvg = buildQrStationArtworkSvg(artwork.options, artwork.qrSvgMarkup);

  return createPortal(
    <div className="qr-print-document" aria-hidden="true">
      <img
        className="qr-print-poster"
        src={svgDataUri(posterSvg)}
        alt=""
      />
    </div>,
    document.body
  );
}
