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
        ws: true,
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
    rolldownOptions: {
      // userPreferences.ts imports the auth service dynamically so a test setup file can seed the
      // store before a test's mocks are in place, not to split a chunk; skip that one warning only.
      onLog(level, log, handler) {
        if (log.code === "INEFFECTIVE_DYNAMIC_IMPORT" && log.id?.endsWith("/src/services/auth.ts"))
          return;
        handler(level, log);
      },
      output: {
        // Third-party code in its own chunks, so the app chunk stays small and a
        // release that only changes app code leaves the vendor chunks cached.
        // entriesAware keeps a library that only a lazy page imports in that
        // page's chunk rather than in the ones every page loads.
        codeSplitting: {
          groups: [
            {
              name: "react",
              test: /node_modules[\\/](react|react-dom|scheduler|react-router)[\\/]/,
              entriesAware: true,
            },
            {
              name: "markdown",
              test: /node_modules[\\/](react-markdown|remark-[^\\/]+|mdast-[^\\/]+|micromark[^\\/]*|unified|hast-[^\\/]+|highlight\.js|lowlight|vfile[^\\/]*|unist-[^\\/]+)[\\/]/,
              entriesAware: true,
            },
            { name: "xterm", test: /node_modules[\\/]@xterm[\\/]/, entriesAware: true },
            { name: "vendor", test: /node_modules[\\/]/, entriesAware: true },
          ],
        },
      },
    },
  },
  test: {
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
    exclude: ["**/node_modules/**", "dist/**"],
    // .ts tests run in node; one that needs a DOM opts in with a "// @vitest-environment jsdom" docblock.
    projects: [
      {
        extends: true,
        test: { name: "jsdom", environment: "jsdom", include: ["src/**/*.test.tsx"] },
      },
      { extends: true, test: { name: "node", environment: "node", include: ["src/**/*.test.ts"] } },
    ],
  },
});
