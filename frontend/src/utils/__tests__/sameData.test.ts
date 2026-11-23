import { describe, expect, test } from "vite-plus/test";
import { sameData } from "../sameData";

describe("sameData", () => {
  test("is true for the same value and for values that serialize alike", () => {
    const a = { id: 1, tags: ["x"] };
    expect(sameData(a, a)).toBe(true);
    expect(sameData(a, { id: 1, tags: ["x"] })).toBe(true);
    expect(sameData(null, null)).toBe(true);
  });

  test("is false when a value differs or one side is empty", () => {
    expect(sameData({ id: 1 }, { id: 2 })).toBe(false);
    expect(sameData(null, { id: 1 })).toBe(false);
  });
});
