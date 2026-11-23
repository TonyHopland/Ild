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

describe("sameData structure", () => {
  test("ignores key order and compares nested data and arrays", () => {
    expect(sameData({ a: 1, b: { c: [1, 2] } }, { b: { c: [1, 2] }, a: 1 })).toBe(true);
    expect(sameData({ a: [1, 2] }, { a: [2, 1] })).toBe(false);
    expect(sameData({ a: 1 }, { a: 1, b: undefined })).toBe(false);
    expect(sameData([], {})).toBe(false);
  });
});
