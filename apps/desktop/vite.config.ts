import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), "");
  return {
    plugins: [react()],
    publicDir: "../portal/public",
    clearScreen: false,
    server: {
      host: "127.0.0.1",
      port: 1420,
      strictPort: true
    },
    envPrefix: ["VITE_", "TAURI_"],
    define: {
      __SMARTPRINT_API_BASE_URL__: JSON.stringify(env.VITE_API_BASE_URL || "")
    },
    build: {
      target: process.env.TAURI_ENV_PLATFORM === "windows" ? "chrome105" : "safari13",
      minify: !process.env.TAURI_ENV_DEBUG ? "esbuild" : false,
      sourcemap: !!process.env.TAURI_ENV_DEBUG
    }
  };
});