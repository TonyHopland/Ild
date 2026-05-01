import { defineConfig } from "vite-plus";

export default defineConfig({
  staged: {
    "*": "vp check --fix",
  },
  fmt: {},
  lint: { options: { typeAware: true, typeCheck: true } },
  test: {
    environment: "jsdom",
    exclude: ["**/data/repos/**", "**/node_modules/**"],
  },
});
