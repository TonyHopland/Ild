import { describe, expect, test } from "vite-plus/test";
import {
  computeLineDiff,
  computeWordDiff,
  createWordDiffBudget,
  isWordDiffBudgetSpent,
} from "./jsonDiff";

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

describe("computeLineDiff intra-line segments", () => {
  const text = (line: { segments?: Array<{ text: string; changed: boolean }> }) =>
    (line.segments ?? []).map((s) => s.text).join("");
  const changed = (line: { segments?: Array<{ text: string; changed: boolean }> }) =>
    (line.segments ?? []).filter((s) => s.changed).map((s) => s.text);

  test("splits a paired del/add line into changed and unchanged runs", () => {
    const [del, add] = computeLineDiff(
      '  "prompt": "Review the diff and stop",',
      '  "prompt": "Review the patch and stop",',
    );

    expect(changed(del)).toEqual(["diff"]);
    expect(changed(add)).toEqual(["patch"]);
    // Segments cover the line in full, so the renderer can shade every run.
    expect(text(del)).toBe('  "prompt": "Review the diff and stop",');
    expect(text(add)).toBe('  "prompt": "Review the patch and stop",');
  });

  test("emphasises only the appended words when a line grows", () => {
    const [del, add] = computeLineDiff("keep going", "keep going until done");
    expect(changed(del)).toEqual([]);
    expect(changed(add)).toEqual([" until done"]);
  });

  test("leaves unpaired insertions and deletions without segments", () => {
    // A pure insertion has no counterpart to compare against; the whole-line
    // treatment is all the reader gets.
    const inserted = computeLineDiff("a\nc", "a\nb\nc");
    expect(inserted.every((l) => l.segments === undefined)).toBeTruthy();

    const removed = computeLineDiff("a\nb\nc", "a\nc");
    expect(removed.every((l) => l.segments === undefined)).toBeTruthy();

    // Three removed lines against one added line: only the first pairs up.
    const uneven = computeLineDiff("one x\ntwo\nthree", "one y");
    expect(uneven.map((l) => l.type)).toEqual(["del", "del", "del", "add"]);
    expect(changed(uneven[0])).toEqual(["x"]);
    expect(uneven[1].segments).toBeUndefined();
    expect(uneven[2].segments).toBeUndefined();
  });

  test("skips segmentation for lines that share almost nothing", () => {
    const diff = computeLineDiff("hello", "world");
    expect(diff).toEqual([
      { type: "del", text: "hello" },
      { type: "add", text: "world" },
    ]);
  });

  test("skips segmentation for a wholesale prompt rewrite", () => {
    // The headline confetti case: two long prompt lines sharing only stopwords
    // and JSON boilerplate. Left whole, the reader sees one changed line; split,
    // they would see ~15 boxes a side pivoting on "the" and "this".
    const diff = computeLineDiff(
      `      "prompt": "Review the current diff of this worktree against the work item. Report correctness problems and stop.",`,
      `      "prompt": "Inspect every changed file on this branch, compare it to the specification, and list any defects you can prove.",`,
    );

    expect(diff.map((l) => l.type)).toEqual(["del", "add"]);
    expect(diff[0].segments).toBeUndefined();
    expect(diff[1].segments).toBeUndefined();
  });

  test("falls back to whole-line highlighting for a pair too large to align", () => {
    // ~1400 distinct tokens a side, so the intra-line table blows the cell cap
    // and the pair keeps the light tier instead of stalling the save modal.
    const before = Array.from({ length: 700 }, (_, i) => `a${i}`).join(" ");
    const after = Array.from({ length: 700 }, (_, i) => `b${i}`).join(" ");

    const diff = computeLineDiff(before, after);

    expect(diff.map((l) => l.type)).toEqual(["del", "add"]);
    expect(diff[0].segments).toBeUndefined();
    expect(diff[1].segments).toBeUndefined();
  });

  test("spends one alignment budget across the whole document", () => {
    // Six pairs that each cost ~234k cells (their first and last words differ, so
    // nothing trims and the full token table is built) against a 1M aggregate
    // budget: four fit, the rest keep the whole-line tier. Without a shared
    // budget every one of them would pay, and 200 of them could.
    const shared = Array.from({ length: 240 }, (_, i) => `w${i}`).join(" ");
    const line = (head: string, tail: string) => `${head} ${shared} ${tail}`;
    const before = Array.from({ length: 6 }, (_, i) => line(`p${i}`, `s${i}a`)).join("\n");
    const after = Array.from({ length: 6 }, (_, i) => line(`q${i}`, `s${i}b`)).join("\n");

    const dels = computeLineDiff(before, after).filter((l) => l.type === "del");

    expect(dels).toHaveLength(6);
    expect(changed(dels[0])).toEqual(["p0", "s0a"]);
    expect(dels[3].segments).toBeDefined();
    expect(dels[4].segments).toBeUndefined();
    expect(dels[5].segments).toBeUndefined();
  });

  test("stops segmenting past the paired-line cap", () => {
    // 250 paired lines: the first 200 are segmented, the rest degrade to the
    // whole-line tier rather than paying for 250 nested diffs.
    const n = 250;
    const before = Array.from({ length: n }, (_, i) => `del line ${i}`).join("\n");
    const after = Array.from({ length: n }, (_, i) => `add line ${i}`).join("\n");

    const diff = computeLineDiff(before, after);
    const dels = diff.filter((l) => l.type === "del");
    const adds = diff.filter((l) => l.type === "add");

    expect(dels).toHaveLength(n);
    expect(adds).toHaveLength(n);
    expect(changed(dels[0])).toEqual(["del"]);
    expect(changed(adds[0])).toEqual(["add"]);
    expect(dels[199].segments).toBeDefined();
    expect(dels[200].segments).toBeUndefined();
    expect(adds[200].segments).toBeUndefined();
    expect(dels[n - 1].segments).toBeUndefined();
  });
});

describe("computeWordDiff — cost caps", () => {
  test("counts the alignment table's sentinel row and column", () => {
    // The table is (n+1)x(m+1), so a pair whose token counts multiply to exactly
    // the per-pair cap has already overspent it. These two pairs sit either side
    // of that boundary — 499 tokens a side needs a 500x500 table and fits, 500
    // needs 501x501 and does not — and differ only at their ends, so nothing
    // trims and the similarity guard cannot be what rejects the larger one.
    // Both assertions flip if MAX_INTRA_LINE_CELLS moves, which is the point:
    // the boundary should be re-derived rather than silently stop being one.
    const shared = Array.from({ length: 248 }, (_, i) => `w${i}`).join(" ");
    // The tails differ from each other, so the extra token in the second pair
    // survives the common-suffix trim instead of being stripped off both lines.
    const pair = (tailA: string, tailB: string) =>
      [`p ${shared} ${tailA}`, `q ${shared} ${tailB}`] as const;

    const [fitsBefore, fitsAfter] = pair("sa", "sb");
    const [overBefore, overAfter] = pair("sa.", "sb,");

    expect(computeWordDiff(fitsBefore, fitsAfter)).not.toBeNull();
    expect(computeWordDiff(overBefore, overAfter)).toBeNull();
  });
});

/**
 * `isWordDiffBudgetSpent` lets a caller stop walking a document once no further
 * pair can be segmented. Each case pairs the predicate with what
 * `computeWordDiff` actually does on that budget, because the failure mode is
 * the two drifting apart: a predicate that says "spent" too early silently drops
 * segments a reader should have seen.
 */
describe("isWordDiffBudgetSpent", () => {
  const cheap = ["keep this b", "keep this c"] as const;

  test("a fresh budget is not spent", () => {
    const budget = createWordDiffBudget();
    expect(isWordDiffBudgetSpent(budget)).toBe(false);
    expect(computeWordDiff(...cheap, budget)).not.toBeNull();
  });

  test("out of pairs is spent, and every further pair is refused", () => {
    const budget = { cells: 1_000_000, pairs: 0 };
    expect(isWordDiffBudgetSpent(budget)).toBe(true);
    expect(computeWordDiff(...cheap, budget)).toBeNull();
  });

  test("out of cells is spent, because the smallest pair still costs one", () => {
    const budget = { cells: 0, pairs: 200 };
    expect(isWordDiffBudgetSpent(budget)).toBe(true);
    expect(computeWordDiff(...cheap, budget)).toBeNull();
  });

  test("cells left but not enough for the pair in hand is not spent", () => {
    // The distinction the predicate exists to draw: this pair is refused, and a
    // cheaper one after it still fits. Stopping the walk here would lose it.
    const budget = { cells: 10, pairs: 5 };
    expect(computeWordDiff("p1 p2 p3 p4 p5", "q1 q2 q3 q4 q5", budget)).toBeNull();
    expect(isWordDiffBudgetSpent(budget)).toBe(false);
    expect(computeWordDiff(...cheap, budget)).not.toBeNull();
  });
});

/**
 * Calibration of the "is this pair worth segmenting?" decision, asserted on the
 * line pair itself rather than through a document contrived to make the line
 * diff emit that pair. Every line here is short enough that the cost caps cannot
 * fire, so a null result means the two lines were judged too dissimilar.
 */
describe("computeWordDiff — worth segmenting?", () => {
  const prompt = (value: string) => `      "prompt": ${JSON.stringify(value)},`;
  const review =
    "Review the current diff of this worktree against the work item. Report correctness problems and stop.";

  const segmented: Array<[string, string, string]> = [
    ["one word replaced", prompt(review), prompt(review.replace("diff", "patch"))],
    [
      "a clause appended",
      prompt("Fix every finding — do not argue with the review."),
      prompt("Fix every finding — do not argue with the review and do not skip items."),
    ],
    [
      "half the sentence reworded",
      prompt("Review the current diff of this worktree against the work item."),
      prompt("Review the current diff of this branch against the acceptance criteria."),
    ],
    ["a short value replaced outright", prompt("Do the thing"), prompt("Handle everything else")],
    ["a node id changed", '      "id": "old-node",', '      "id": "qa",'],
  ];

  const whole: Array<[string, string, string]> = [
    [
      "a wholesale rewrite of the same key",
      prompt(review),
      prompt(
        "Inspect every changed file on this branch, compare it to the specification, and list any defects you can prove.",
      ),
    ],
    [
      "two long prompts with no word in common",
      prompt(Array.from({ length: 20 }, (_, i) => `w${i}${i}`).join(" ")),
      prompt(Array.from({ length: 20 }, (_, i) => `${i}z${i}`).join(" ")),
    ],
    [
      "two different keys",
      '      "label": "Run the test suite",',
      '      "prompt": "Summarize the repository layout",',
    ],
    ["two unrelated words", "hello", "world"],
  ];

  for (const [name, before, after] of segmented) {
    test(`segments ${name}`, () => {
      const words = computeWordDiff(before, after);
      expect(words).not.toBeNull();
      // Whatever it emphasises, the segments still reproduce both lines exactly.
      expect(words?.del.map((s) => s.text).join("")).toBe(before);
      expect(words?.add.map((s) => s.text).join("")).toBe(after);
      // ...and something is actually emphasised, on the added side at minimum
      // (a line that only grew has nothing to mark on the removed side).
      expect(words?.add.some((s) => s.changed)).toBeTruthy();
    });
  }

  for (const [name, before, after] of whole) {
    test(`leaves ${name} whole`, () => {
      expect(computeWordDiff(before, after)).toBeNull();
    });
  }
});
