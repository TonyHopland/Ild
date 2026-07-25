import { describe, expect, test } from "vite-plus/test";
import { parseUnifiedDiff, DiffRow } from "../unifiedDiff";

/** The strong-tier runs of a row, in order — what the viewer emphasises. */
const changed = (row: DiffRow) => (row.segments ?? []).filter((s) => s.changed).map((s) => s.text);

/** Whatever it emphasises, the segments must still reproduce the line's payload. */
const payload = (row: DiffRow) => (row.segments ?? []).map((s) => s.text).join("");

const patch = (...lines: string[]) => lines.join("\n");

describe("parseUnifiedDiff — classification", () => {
  test("classifies hunk headers, content lines and context", () => {
    const rows = parseUnifiedDiff(patch("@@ -1,2 +1,2 @@", " kept", "-gone", "+new"));

    expect(rows.map((r) => r.kind)).toEqual(["hunk", "ctx", "del", "add"]);
    // The raw line survives classification, marker and all.
    expect(rows.map((r) => r.text)).toEqual(["@@ -1,2 +1,2 @@", " kept", "-gone", "+new"]);
  });

  test("ignores a single trailing newline rather than emitting a blank row", () => {
    const rows = parseUnifiedDiff("@@ -1 +1 @@\n-gone\n+new\n");
    expect(rows).toHaveLength(3);
  });
});

describe("parseUnifiedDiff — pairing", () => {
  test("emphasises the words that differ between a paired del/add line", () => {
    const [, del, add] = parseUnifiedDiff(
      patch("@@ -1 +1 @@", "-const timeout = 30;", "+const timeout = 60;"),
    );

    expect(changed(del)).toEqual(["30"]);
    expect(changed(add)).toEqual(["60"]);
    // Segments cover the payload only — the marker is the renderer's business.
    expect(payload(del)).toBe("const timeout = 30;");
    expect(payload(add)).toBe("const timeout = 60;");
  });

  test("leaves an unpaired insertion or deletion whole", () => {
    // Neither hunk has a counterpart to compare against, and borrowing one from
    // the context around it would emphasise words nobody edited.
    const rows = parseUnifiedDiff(
      patch(
        "@@ -1,2 +1,1 @@",
        " keep this",
        "-dropped entirely",
        "@@ -10,1 +10,2 @@",
        " keep this",
        "+added entirely",
      ),
    );

    expect(rows.every((r) => r.segments === undefined)).toBeTruthy();
  });

  test("keeps a pair that is not worth segmenting whole", () => {
    // computeWordDiff declines two lines sharing nothing; that is a normal
    // result, and the line still shades — just in one tier.
    const [, del, add] = parseUnifiedDiff(patch("@@ -1 +1 @@", "-hello", "+world"));

    expect(del.segments).toBeUndefined();
    expect(add.segments).toBeUndefined();
  });

  test("pairs index-wise and stops at the shorter run", () => {
    const [, first, second, third, add] = parseUnifiedDiff(
      patch(
        "@@ -1,3 +1,1 @@",
        "-step one alpha",
        "-step two beta",
        "-step three gamma",
        "+step one omega",
      ),
    );

    expect(changed(first)).toEqual(["alpha"]);
    expect(changed(add)).toEqual(["omega"]);
    // Removed lines past the end of the added run have no counterpart.
    expect(second.segments).toBeUndefined();
    expect(third.segments).toBeUndefined();
  });

  test("truncates the same way when the added run is the longer one", () => {
    const [, del, first, second, third] = parseUnifiedDiff(
      patch(
        "@@ -1,1 +1,3 @@",
        "-step one alpha",
        "+step one omega",
        "+step two beta",
        "+step three gamma",
      ),
    );

    expect(changed(del)).toEqual(["alpha"]);
    expect(changed(first)).toEqual(["omega"]);
    expect(second.segments).toBeUndefined();
    expect(third.segments).toBeUndefined();
  });

  test("pairs each run of a multi-hunk patch independently", () => {
    const rows = parseUnifiedDiff(
      patch(
        "@@ -1,2 +1,2 @@",
        "-const timeout = 30;",
        "-const retries = 1;",
        "+const timeout = 60;",
        "+const retries = 5;",
        "@@ -20,1 +20,1 @@",
        " untouched",
        "-const backoff = 100;",
        "+const backoff = 250;",
      ),
    );

    expect(rows.filter((r) => r.segments).map(changed)).toEqual([
      ["30"],
      ["1"],
      ["60"],
      ["5"],
      ["100"],
      ["250"],
    ]);
  });

  test("a context line between two runs separates them", () => {
    // The added run has to follow the removed one immediately; anything else
    // means these are two independent edits that happen to be adjacent.
    const rows = parseUnifiedDiff(
      patch("@@ -1,3 +1,3 @@", "-const timeout = 30;", " untouched", "+const timeout = 60;"),
    );

    expect(rows.every((r) => r.segments === undefined)).toBeTruthy();
  });
});

/**
 * A unified diff is not a list of prefixed payloads: `+` and `-` appear on lines
 * that are not content, and content appears on lines that look like they are not.
 * Getting this wrong emphasises words nobody edited, so each shape is pinned.
 */
describe("parseUnifiedDiff — lines that only look like content", () => {
  test("does not pair the patch preamble's --- / +++ headers", () => {
    // Prefix matching would pair them and emphasise the a/ and b/ that are
    // supposed to differ, so the walk keys off the hunk header instead.
    const rows = parseUnifiedDiff(
      patch(
        "diff --git a/a.ts b/a.ts",
        "index 1111111..2222222 100644",
        "--- a/a.ts",
        "+++ b/a.ts",
        "@@ -1 +1 @@",
        "-const timeout = 30;",
        "+const timeout = 60;",
      ),
    );
    const [, , minus, plus, , del, add] = rows;

    expect(minus.text).toBe("--- a/a.ts");
    expect(plus.text).toBe("+++ b/a.ts");
    expect(minus.segments).toBeUndefined();
    expect(plus.segments).toBeUndefined();
    // Header shading is longstanding behaviour, pinned so a change to it is a
    // deliberate one rather than a side effect.
    expect(minus.kind).toBe("del");
    expect(plus.kind).toBe("add");
    // The content pair below them still segments.
    expect(changed(del)).toEqual(["30"]);
    expect(changed(add)).toEqual(["60"]);
  });

  test("treats a deleted line that itself starts with -- as content", () => {
    // "-- note" arrives with its marker as "--- note", indistinguishable from a
    // file header by prefix alone — inside a hunk it is an ordinary removal.
    const [, del, add] = parseUnifiedDiff(
      patch("@@ -1 +1 @@", "--- keep the first note", "+-- keep the second note"),
    );

    expect(changed(del)).toEqual(["first"]);
    expect(changed(add)).toEqual(["second"]);
    expect(payload(del)).toBe("-- keep the first note");
    expect(payload(add)).toBe("-- keep the second note");
  });

  test("segments across a no-newline-at-end-of-file marker", () => {
    // git puts the marker between the removed and added sides of the last line,
    // which is exactly the pair worth segmenting — it annotates the line before
    // it rather than ending the run.
    const [, del, marker, add] = parseUnifiedDiff(
      patch(
        "@@ -1 +1 @@",
        "-const timeout = 30;",
        "\\ No newline at end of file",
        "+const timeout = 60;",
        "\\ No newline at end of file",
      ),
    );

    expect(changed(del)).toEqual(["30"]);
    expect(changed(add)).toEqual(["60"]);
    expect(marker.kind).toBe("ctx");
    expect(marker.segments).toBeUndefined();
  });

  test("a trailing no-newline marker with no added run does not wedge the walk", () => {
    const rows = parseUnifiedDiff(
      patch("@@ -1 +0,0 @@", "-const timeout = 30;", "\\ No newline at end of file"),
    );

    expect(rows.map((r) => r.kind)).toEqual(["hunk", "del", "ctx"]);
    expect(rows.every((r) => r.segments === undefined)).toBeTruthy();
  });
});

/**
 * The word alignment is quadratic, so {@link parseUnifiedDiff} draws one
 * allowance for the whole patch rather than one per pair. Both halves of that
 * are asserted here, because both stay green if the budget is moved inside the
 * pairing loop — where a 1M-cell allowance quietly becomes 200 x 250k and the
 * paired-line cap stops binding at all.
 */
describe("parseUnifiedDiff — one alignment budget per patch", () => {
  test("stops segmenting past the 200-pair cap", () => {
    // Cheap pairs, so the cell allowance is nowhere near spent and the
    // paired-line cap is unambiguously what stops the 201st.
    const n = 201;
    const dels = Array.from({ length: n }, (_, i) => `-const timeout${i} = 30;`);
    const adds = Array.from({ length: n }, (_, i) => `+const timeout${i} = 60;`);

    const rows = parseUnifiedDiff(patch(`@@ -1,${n} +1,${n} @@`, ...dels, ...adds));
    const del = rows.filter((r) => r.kind === "del");
    const add = rows.filter((r) => r.kind === "add");

    expect(del).toHaveLength(n);
    expect(changed(del[0])).toEqual(["30"]);
    expect(del[199].segments).toBeDefined();
    expect(del[200].segments).toBeUndefined();
    expect(add[200].segments).toBeUndefined();
  });

  test("spends one cell allowance across every pair in the patch", () => {
    // Six pairs that each cost ~234k cells — their first and last words differ,
    // so nothing trims — against a 1M aggregate allowance: four fit and the rest
    // keep the whole-line tier. Per-pair budgets would segment all six.
    const shared = Array.from({ length: 240 }, (_, i) => `w${i}`).join(" ");
    const dels = Array.from({ length: 6 }, (_, i) => `-p${i} ${shared} s${i}a`);
    const adds = Array.from({ length: 6 }, (_, i) => `+q${i} ${shared} s${i}b`);

    const rows = parseUnifiedDiff(patch("@@ -1,6 +1,6 @@", ...dels, ...adds));
    const del = rows.filter((r) => r.kind === "del");

    expect(changed(del[0])).toEqual(["p0", "s0a"]);
    expect(del[3].segments).toBeDefined();
    expect(del[4].segments).toBeUndefined();
    expect(del[5].segments).toBeUndefined();
  });
});
