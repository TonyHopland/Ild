import { describe, expect, test } from "vite-plus/test";
import { computeLineDiff } from "./jsonDiff";

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
    const diff = computeLineDiff("a\nb\n", "a\nb");
    expect(diff.every((l) => l.type === "context")).toBeTruthy();
  });

  test("trims common prefix/suffix so a localized edit stays small", () => {
    const before = "h1\nh2\nX\nt1\nt2";
    const after = "h1\nh2\nY\nt1\nt2";
    const diff = computeLineDiff(before, after);
    expect(diff).toEqual([
      { type: "context", text: "h1" },
      { type: "context", text: "h2" },
      { type: "del", text: "X" },
      { type: "add", text: "Y" },
      { type: "context", text: "t1" },
      { type: "context", text: "t2" },
    ]);
  });

  test("degrades to a block replace for a very large changed region (no quadratic blowup)", () => {
    // 1100 x 1100 = 1.21M cells > the LCS cap, so the middle is block-replaced
    // rather than aligned line-by-line — the guard against freezing on ~1 MB docs.
    const n = 1100;
    const before = ["HEAD", ...Array.from({ length: n }, (_, i) => `a${i}`), "TAIL"].join("\n");
    const after = ["HEAD", ...Array.from({ length: n }, (_, i) => `b${i}`), "TAIL"].join("\n");

    const diff = computeLineDiff(before, after);

    expect(diff[0]).toEqual({ type: "context", text: "HEAD" });
    expect(diff[diff.length - 1]).toEqual({ type: "context", text: "TAIL" });
    const mid = diff.slice(1, -1);
    const firstAdd = mid.findIndex((l) => l.type === "add");
    expect(mid.slice(0, firstAdd).every((l) => l.type === "del")).toBeTruthy();
    expect(mid.slice(firstAdd).every((l) => l.type === "add")).toBeTruthy();
    expect(mid.some((l) => l.type === "context")).toBeFalsy();
  });
});
