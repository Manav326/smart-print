import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const includePhotoStudioQaFixture = process.env.VITE_ENABLE_PHOTO_STUDIO_QA === "true";

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
  build: includePhotoStudioQaFixture ? {
    rollupOptions: {
      input: {
        app: "index.html",
        photoStudioQa: "tests/photo-studio.browser.html"
      }
    }
  } : undefined,
  preview: { port: 4173, strictPort: true }
});
