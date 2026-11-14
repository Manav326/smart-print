export type SupportedPhotoMimeType = "image/jpeg" | "image/png";

export function resolvePhotoSourceMimeType(
  source: Pick<File, "type" | "name">
): SupportedPhotoMimeType | null {
  const declaredMime = source.type.split(";")[0].trim().toLowerCase();
  if (declaredMime) {
    return declaredMime === "image/jpeg" || declaredMime === "image/png"
      ? declaredMime
      : null;
  }

  // Android content providers sometimes return a valid JPEG/PNG with no MIME
  // type. Only use the filename in that case; never relabel a known WebP/HEIC
  // image as JPEG just because its filename has a misleading extension.
  const extension = source.name.toLowerCase().split(".").pop();
  if (extension === "jpg" || extension === "jpeg") return "image/jpeg";
  if (extension === "png") return "image/png";
  return null;
}

export function isSupportedPhotoSource(source: Pick<File, "type" | "name">): boolean {
  return resolvePhotoSourceMimeType(source) !== null;
}
