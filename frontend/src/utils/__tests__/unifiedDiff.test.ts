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

/** Each row's [original, current] line numbers, `undefined` where a column is blank. */
const numbers = (rows: DiffRow[]) => rows.map((r) => [r.oldLine, r.newLine]);

describe("parseUnifiedDiff — line numbers", () => {
  test("numbers context, removed and added lines from the hunk header", () => {
    const rows = parseUnifiedDiff(
      patch("@@ -3,4 +3,4 @@", " before", "-gone", "+new", " after", " last"),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [3, 3],
      [4, undefined],
      [undefined, 4],
      [5, 5],
      [6, 6],
    ]);
  });

  test("advances only the side a removal or addition belongs to", () => {
    const rows = parseUnifiedDiff(
      patch("@@ -10,4 +20,3 @@", "-one", "-two", "+uno", " same", "-three", " end"),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [10, undefined],
      [11, undefined],
      [undefined, 20],
      [12, 21],
      [13, undefined],
      [14, 22],
    ]);
  });

  test("restarts both counters at every hunk header", () => {
    const rows = parseUnifiedDiff(
      patch("@@ -1,2 +1,3 @@", " a", "+b", " c", "@@ -10,3 +12,4 @@", " x", "+y", " z", " w"),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [1, 1],
      [undefined, 2],
      [2, 3],
      [undefined, undefined],
      [10, 12],
      [undefined, 13],
      [11, 14],
      [12, 15],
    ]);
  });

  test("treats an omitted count as 1 and ignores the function context after @@", () => {
    const rows = parseUnifiedDiff(
      patch(
        "@@ -7 +9 @@ function f(a = 99) {",
        "-old",
        "+new",
        "@@ -40,2 +42 @@ class C@@ -1,1 +1,1 @@",
        " keep",
        "-drop",
      ),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [7, undefined],
      [undefined, 9],
      [undefined, undefined],
      [40, 42],
      [41, undefined],
    ]);
  });

  test("leaves the patch preamble unnumbered, --- and +++ headers included", () => {
    const rows = parseUnifiedDiff(
      patch(
        "diff --git a/a.ts b/a.ts",
        "new file mode 100644",
        "index 0000000..2222222",
        "--- /dev/null",
        "+++ b/a.ts",
        "@@ -0,0 +1,2 @@",
        "+first",
        "+second",
      ),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [undefined, undefined],
      [undefined, undefined],
      [undefined, undefined],
      [undefined, undefined],
      [undefined, undefined],
      [undefined, 1],
      [undefined, 2],
    ]);
  });

  test("numbers a content line that starts with --- or +++ inside a hunk", () => {
    const rows = parseUnifiedDiff(
      patch("@@ -4,2 +4,2 @@", "--- a note", "+++ b note", " --- context"),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [4, undefined],
      [undefined, 4],
      [5, 5],
    ]);
  });

  test("leaves a no-newline-at-end-of-file marker unnumbered and uncounted", () => {
    const rows = parseUnifiedDiff(
      patch(
        "@@ -1,2 +1,2 @@",
        " kept",
        "-const timeout = 30;",
        "\\ No newline at end of file",
        "+const timeout = 60;",
        "\\ No newline at end of file",
      ),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [1, 1],
      [2, undefined],
      [undefined, undefined],
      [undefined, 2],
      [undefined, undefined],
    ]);
  });

  test("numbers nothing in a diff that is not a patch", () => {
    const rows = parseUnifiedDiff(
      patch("diff --git a/logo.png b/logo.png", "Binary files a/logo.png and b/logo.png differ"),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [undefined, undefined],
    ]);
  });

  test("numbers a brand-new file 1..N in the current column", () => {
    const rows = parseUnifiedDiff(patch("@@ -0,0 +1,3 @@", "+a", "+b", "+c"));

    expect(numbers(rows.slice(1))).toEqual([
      [undefined, 1],
      [undefined, 2],
      [undefined, 3],
    ]);
  });

  test("numbers a deleted file 1..N in the original column", () => {
    const rows = parseUnifiedDiff(patch("@@ -1,3 +0,0 @@", "-a", "-b", "-c"));

    expect(numbers(rows.slice(1))).toEqual([
      [1, undefined],
      [2, undefined],
      [3, undefined],
    ]);
  });

  test("numbers a pure insertion and a pure deletion from their headers", () => {
    const rows = parseUnifiedDiff(
      patch("@@ -5,0 +6,2 @@", "+six", "+seven", "@@ -6,2 +5,0 @@", "-six", "-seven"),
    );

    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [undefined, 6],
      [undefined, 7],
      [undefined, undefined],
      [6, undefined],
      [7, undefined],
    ]);
  });

  test("shows no numbers under a header it cannot parse, until the next valid one", () => {
    const rows = parseUnifiedDiff(
      patch("@@ -1,2 +1,2 @@", " a", "@@ nonsense @@", " b", "-c", "+d", "@@ -30,1 +31,1 @@", " e"),
    );

    expect(rows.map((r) => r.text)).toHaveLength(8);
    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [1, 1],
      [undefined, undefined],
      [undefined, undefined],
      [undefined, undefined],
      [undefined, undefined],
      [undefined, undefined],
      [30, 31],
    ]);
  });

  test("numbers every line even once the word-diff budget is spent", () => {
    // Past the 200-pair cap the pairing walk stops; numbering must not stop with it.
    const n = 250;
    const dels = Array.from({ length: n }, (_, i) => `-const timeout${i} = 30;`);
    const adds = Array.from({ length: n }, (_, i) => `+const timeout${i} = 60;`);

    const rows = parseUnifiedDiff(
      patch(`@@ -100,${n + 1} +500,${n + 1} @@`, ...dels, ...adds, " tail"),
    );
    const del = rows.filter((r) => r.kind === "del");
    const add = rows.filter((r) => r.kind === "add");

    expect(del[n - 1].segments).toBeUndefined();
    expect(del.map((r) => r.oldLine)).toEqual(Array.from({ length: n }, (_, i) => 100 + i));
    expect(add.map((r) => r.newLine)).toEqual(Array.from({ length: n }, (_, i) => 500 + i));
    expect(del.every((r) => r.newLine === undefined)).toBeTruthy();
    expect(add.every((r) => r.oldLine === undefined)).toBeTruthy();
    expect(numbers(rows.slice(-1))).toEqual([[100 + n, 500 + n]]);
  });

  test("numbers a pair it declined to segment", () => {
    const rows = parseUnifiedDiff(patch("@@ -8 +8 @@", "-hello", "+world"));

    expect(rows[1].segments).toBeUndefined();
    expect(numbers(rows)).toEqual([
      [undefined, undefined],
      [8, undefined],
      [undefined, 8],
    ]);
  });
});
