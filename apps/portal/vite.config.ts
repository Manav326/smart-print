import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const includePhotoStudioQaFixture = process.env.VITE_ENABLE_PHOTO_STUDIO_QA === "true";
const buildAndroidOfflinePhotoStudio = process.env.VITE_BUILD_ANDROID_OFFLINE_PHOTO_STUDIO === "true";

const androidOfflineInputs = {
  photoStudioOffline: "photo-studio-offline.html",
  ...(includePhotoStudioQaFixture ? { photoStudioQa: "tests/photo-studio.browser.html" } : {})
};

export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    strictPort: true,
    proxy: {
      "/api": { target: "http://localhost:8080", changeOrigin: true },
      "/health": { target: "http://localhost:8080", changeOrigin: true }
    }
  },
  build: buildAndroidOfflinePhotoStudio
    ? {
        outDir: "dist-android-offline",
        emptyOutDir: true,
        rollupOptions: { input: androidOfflineInputs }
      }
    : includePhotoStudioQaFixture
      ? {
          rollupOptions: {
            input: {
              app: "index.html",
              photoStudioQa: "tests/photo-studio.browser.html"
            }
          }
        }
      : undefined,
  preview: { port: 4173, strictPort: true }
});
