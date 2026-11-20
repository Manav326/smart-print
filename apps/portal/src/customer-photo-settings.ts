export type CustomerPhotoSettings = {
  sizeCode: string;
  photoCount: number;
  photosPerPage?: number;
  photoOutputLayout?: "A4_SHEET" | "PHOTO_PAPER";
  photoPaperFinish?: "PLAIN" | "GLOSSY" | "MATTE";
};

const maximumPhotosPerA4Page: Readonly<Record<string, number>> = {
  "35x45": 25,
  "25x35": 49,
  "2x2": 15,
  "4x6": 1
};

/**
 * Validates settings restored from the native customer draft before they are
 * reused for a quote or upload. Keep this policy aligned with the API and Agent:
 * A4 sheets allow plain/glossy/matte stock; individual photo paper allows
 * glossy/matte only and always produces one photo per physical sheet.
 */
export function isValidCustomerPhotoSettings(value: unknown): value is CustomerPhotoSettings {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;

  const settings = value as CustomerPhotoSettings;
  const maximum = Object.prototype.hasOwnProperty.call(maximumPhotosPerA4Page, settings.sizeCode)
    ? maximumPhotosPerA4Page[settings.sizeCode]
    : undefined;
  const outputLayout = settings.photoOutputLayout ?? "A4_SHEET";
  const finish = settings.photoPaperFinish ?? (outputLayout === "A4_SHEET" ? "PLAIN" : undefined);

  if (maximum === undefined ||
      !Number.isInteger(settings.photoCount) ||
      settings.photoCount < 1 ||
      settings.photoCount > 100 ||
      !Number.isInteger(settings.photosPerPage) ||
      settings.photosPerPage! < 1 ||
      settings.photosPerPage! > maximum) {
    return false;
  }

  if (outputLayout === "A4_SHEET") {
    return finish === "PLAIN" || finish === "GLOSSY" || finish === "MATTE";
  }

  if (outputLayout === "PHOTO_PAPER") {
    return (finish === "GLOSSY" || finish === "MATTE") && settings.photosPerPage === 1;
  }

  return false;
}
