import { createRoot } from "react-dom/client";
import React from "react";
import ShopDocumentPreviewSurface from "../src/ShopDocumentPreviewSurface";

const canvas = document.createElement("canvas");
canvas.width = 160;
canvas.height = 120;
const drawing = canvas.getContext("2d");
if (!drawing) throw new Error("Could not initialise the image-preview fixture canvas.");
drawing.fillStyle = "#245b94";
drawing.fillRect(0, 0, canvas.width, canvas.height);
drawing.fillStyle = "#f5c76c";
drawing.beginPath();
drawing.arc(80, 60, 30, 0, Math.PI * 2);
drawing.fill();
const pngUrl = canvas.toDataURL("image/png");
const jpegUrl = canvas.toDataURL("image/jpeg", 0.92);

createRoot(document.getElementById("root")!).render(
  <main>
    <section id="pdf-preview">
      <ShopDocumentPreviewSurface name="preview-fixture.pdf" url="/fixtures/preview-fixture.pdf" mediaType="application/pdf" />
    </section>
    <section id="jpeg-preview">
      <ShopDocumentPreviewSurface name="preview-fixture.jpg" url={jpegUrl} mediaType="image/jpeg" printKind="PHOTO" />
    </section>
    <section id="png-preview">
      <ShopDocumentPreviewSurface name="preview-fixture.png" url={pngUrl} mediaType="image/png" />
    </section>
    <section id="unsupported-preview">
      <ShopDocumentPreviewSurface name="unsupported.docx" url="/fixtures/unsupported.docx" mediaType="application/vnd.openxmlformats-officedocument.wordprocessingml.document" />
    </section>
    <section id="failed-image-preview">
      <ShopDocumentPreviewSurface name="missing.jpg" url="/fixtures/missing-image.jpg" mediaType="image/jpeg" />
    </section>
  </main>
);
