export type PhotoMediaCapabilityState = "supported" | "unsupported" | "unknown";

export interface PhotoMediaCapabilities {
  /** Exact driver-reported media names that can satisfy a Glossy or Matte job. */
  mediaTypes: string[];
  /** Deduplicated customer-facing finish labels. */
  labels: string[];
  state: PhotoMediaCapabilityState;
}

const printSchemaPhotoFinishes: Readonly<Record<string, "Glossy" | "Matte">> = {
  PhotographicGlossy: "Glossy",
  PhotographicHighGloss: "Glossy",
  PhotographicMatte: "Matte"
};

function legacyPhotoFinish(value: string): "Glossy" | "Matte" | null {
  const normalized = value.replace(/[^a-z0-9]/gi, "").toLowerCase();
  if (!normalized ||
      normalized.includes("semigloss") ||
      normalized.includes("satin") ||
      normalized.includes("lustre") ||
      normalized.includes("luster")) {
    return null;
  }

  if (normalized.includes("photographicglossy") ||
      normalized.includes("glossyphoto") ||
      normalized.includes("glossy") ||
      normalized.includes("highgloss") ||
      normalized.includes("gloss")) {
    return "Glossy";
  }

  if (normalized.includes("photographicmatte") ||
      normalized.includes("mattephoto") ||
      normalized.includes("matte") ||
      normalized.includes("matt")) {
    return "Matte";
  }

  return null;
}

/**
 * Only classify media as photo stock when the driver reports an actual glossy
 * or matte option. Generic Photographic, semi-gloss, satin and lustre are
 * distinct settings and must not be advertised as interchangeable finishes.
 */
export function resolvePhotoMediaCapabilities(
  pageMediaTypes: readonly string[],
  legacyMediaTypes: readonly string[],
  pageMediaTypesStatus?: string,
  legacyMediaTypesStatus?: string
): PhotoMediaCapabilities {
  const schemaMedia = pageMediaTypes.filter(value =>
    Object.prototype.hasOwnProperty.call(printSchemaPhotoFinishes, value)
  );
  const legacyMedia = legacyMediaTypes.filter(value => legacyPhotoFinish(value) !== null);
  const mediaTypes = [...new Set([...schemaMedia, ...legacyMedia])];
  const labels = [...new Set(mediaTypes.map(value =>
    printSchemaPhotoFinishes[value] ?? legacyPhotoFinish(value)
  ).filter((value): value is "Glossy" | "Matte" => value !== null && value !== undefined))];

  const hasAnyReportedMedia =
    pageMediaTypes.length > 0 ||
    legacyMediaTypes.length > 0 ||
    pageMediaTypesStatus === "supported" ||
    pageMediaTypesStatus === "unsupported" ||
    legacyMediaTypesStatus === "supported" ||
    legacyMediaTypesStatus === "unsupported";

  return {
    mediaTypes,
    labels,
    state: labels.length > 0 ? "supported" : hasAnyReportedMedia ? "unsupported" : "unknown"
  };
}
