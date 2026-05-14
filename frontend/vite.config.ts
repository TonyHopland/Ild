import { defineConfig } from "vite-plus";
import react from "@vitejs/plugin-react";

const apiProxyTarget =
  (globalThis as { process?: { env?: Record<string, string | undefined> } }).process?.env
    ?.ILD_API_PROXY_TARGET ?? "http://localhost:5000";

export default defineConfig({
  plugins: react(),
  server: {
    port: 3000,
    proxy: {
      "/api": {
        target: apiProxyTarget,
        changeOrigin: true,
      },
      "/hubs": {
        target: apiProxyTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    outDir: "dist",
  },
  test: {
    environment: "jsdom",
    globals: true,
    exclude: ["dist/**"],
  },
});
