import { describe, expect, test } from "vite-plus/test";
import { sameTag } from "../providerTags";

describe("sameTag", () => {
  test("matches trimmed and case-insensitively", () => {
    expect(sameTag(" qa ", "QA")).toBe(true);
  });

  test("keeps letters apart that the server's per-character upper-casing keeps apart", () => {
    expect(sameTag("straße", "STRASSE")).toBe(false);
    expect(sameTag("ﬁx", "FIX")).toBe(false);
  });
});
