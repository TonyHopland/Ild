import { defineConfig } from "vite-plus";

export default defineConfig({
  staged: {
    "*": "vp check --fix",
  },
  fmt: {
    ignorePatterns: ["frontend/dist/**", "dist/**"],
  },
  lint: {
    ignorePatterns: ["frontend/dist/**", "dist/**"],
    options: { typeAware: true, typeCheck: true },
  },
  test: {
    environment: "jsdom",
    exclude: ["**/data/repos/**", "**/node_modules/**", "**/dist/**"],
  },
});
