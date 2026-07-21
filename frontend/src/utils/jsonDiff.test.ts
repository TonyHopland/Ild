import { describe, expect, test } from "vite-plus/test";
import { computeLineDiff, hasChanges } from "./jsonDiff";

describe("computeLineDiff", () => {
  test("marks identical text as all context", () => {
    const diff = computeLineDiff("a\nb\nc", "a\nb\nc");
    expect(diff.every((l) => l.type === "context")).toBeTruthy();
    expect(diff.map((l) => l.text)).toEqual(["a", "b", "c"]);
  });

  test("marks an added line", () => {
    const diff = computeLineDiff("a\nc", "a\nb\nc");
    expect(diff).toEqual([
      { type: "context", text: "a" },
      { type: "add", text: "b" },
      { type: "context", text: "c" },
    ]);
  });

  test("marks a removed line", () => {
    const diff = computeLineDiff("a\nb\nc", "a\nc");
    expect(diff).toEqual([
      { type: "context", text: "a" },
      { type: "del", text: "b" },
      { type: "context", text: "c" },
    ]);
  });

  test("marks a changed line as del then add", () => {
    const diff = computeLineDiff("hello", "world");
    expect(diff).toEqual([
      { type: "del", text: "hello" },
      { type: "add", text: "world" },
    ]);
  });

  test("ignores a trailing newline", () => {
    expect(hasChanges("a\nb\n", "a\nb")).toBeFalsy();
  });

  test("hasChanges is false for identical, true otherwise", () => {
    expect(hasChanges("x", "x")).toBeFalsy();
    expect(hasChanges("x", "y")).toBeTruthy();
  });
});
