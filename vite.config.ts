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
    options: { typeAware: true, typeCheck: true, denyWarnings: true },
  },
  // The frontend's config owns how its tests run; `vp test` from here runs them that same way.
  test: {
    projects: ["frontend"],
  },
});
