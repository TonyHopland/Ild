/**
 * A minimal line-level diff, enough to drive the save-time review modal in the
 * Loop Editor (ADR-0011). The repo carries no diff library and the files-tab
 * DiffView takes an already-computed unified-diff string, so we compute our own
 * line diff between the last-saved and currently-edited loop JSON here.
 */

export type DiffLineType = "context" | "add" | "del";

/** A run of a line's text, flagged with whether this edit touched it. */
export interface DiffSegment {
  text: string;
  changed: boolean;
}

export interface DiffLine {
  type: DiffLineType;
  text: string;
  /**
   * Word-level breakdown of `text`, present only on a del/add line that could be
   * paired with its counterpart (see {@link annotateChangedSegments}).
   * Concatenating the segments reproduces `text` exactly, so a renderer can
   * shade the whole line lightly and emphasise the `changed` runs — the loop
   * documents diffed here are JSON, whose prompt values are single lines
   * thousands of characters long, and shading one of those solid says only "this
   * line changed somewhere". Absent when no useful sub-line diff exists, in
   * which case the line stands on its own.
   */
  segments?: DiffSegment[];
}

/**
 * Cap on the LCS table size (rows × columns). A loop document can be up to ~1 MB
 * on the server, which pretty-prints to tens of thousands of lines; an unbounded
 * O(n·m) table over that would allocate gigabytes and freeze the tab. We first
 * strip the common prefix/suffix — which collapses the usual case (a localized
 * edit) to a tiny changed middle — and only run the quadratic LCS when the
 * remaining middle fits under this cap; otherwise we degrade to a coarse
 * block-replace so the modal still opens instantly.
 */
const MAX_LCS_CELLS = 1_000_000;

/**
 * Same bounded-cost discipline for the intra-line pass, which runs a second LCS
 * per del/add pair: a cap on that pair's table (token counts, after the common
 * token prefix/suffix is trimmed) and a cap on how many pairs get the treatment
 * at all. Exceeding either is not an error — the pair simply keeps no segments
 * and renders as a plain changed line, so a pathological document costs the
 * modal nothing beyond the line diff it already paid for.
 */
const MAX_INTRA_LINE_CELLS = 250_000;
const MAX_INTRA_LINE_PAIRS = 200;

/**
 * A del/add pair where this much of *both* lines would end up emphasised isn't
 * one line edited — it's two unrelated lines that happen to be adjacent (the
 * usual source being the block-replace fallback above). Highlighting nearly
 * everything only adds noise, so those keep the plain whole-line treatment.
 */
const MAX_CHANGED_RATIO = 0.7;

/** Words, whitespace runs, and single punctuation marks. */
const TOKEN_PATTERN = /[\p{L}\p{N}_]+|\s+|[^\p{L}\p{N}_\s]/gu;

/**
 * Line diff between two texts. Emits removed lines (from `before`), added lines
 * (from `after`), and unchanged context lines in source order, each del/add line
 * carrying an optional word-level {@link DiffLine.segments} breakdown of what
 * changed within it. Trailing newlines are ignored so an identical document
 * diffs to all-context with no spurious blank line. Bounded in time and memory:
 * common prefix/suffix are trimmed in O(n+m), and the differing middle is diffed
 * with LCS only while it stays under {@link MAX_LCS_CELLS}, degrading to a block
 * replace for pathologically large inputs.
 */
export function computeLineDiff(before: string, after: string): DiffLine[] {
  const a = splitLines(before);
  const b = splitLines(after);

  // Trim the common prefix and suffix so the quadratic step only sees the part
  // that actually differs — a prompt tweak in a large loop trims to a handful of
  // lines regardless of document size.
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }

  const out: DiffLine[] = [];
  for (let i = 0; i < start; i++) out.push({ type: "context", text: a[i] });

  const midA = a.slice(start, endA);
  const midB = b.slice(start, endB);
  if (midA.length * midB.length > MAX_LCS_CELLS) {
    // Too large to align line-by-line; show the changed region as a removed
    // block followed by an added block rather than risk wedging the UI.
    for (const line of midA) out.push({ type: "del", text: line });
    for (const line of midB) out.push({ type: "add", text: line });
  } else {
    for (const line of diffMiddle(midA, midB)) out.push(line);
  }

  for (let i = endA; i < a.length; i++) out.push({ type: "context", text: a[i] });
  annotateChangedSegments(out);
  return out;
}

/** LCS-backed diff of the already-trimmed differing region. */
function diffMiddle(a: string[], b: string[]): DiffLine[] {
  const n = a.length;
  const m = b.length;
  const lcs = lcsTable(a, b);

  const out: DiffLine[] = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      out.push({ type: "context", text: a[i] });
      i++;
      j++;
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) {
      out.push({ type: "del", text: a[i] });
      i++;
    } else {
      out.push({ type: "add", text: b[j] });
      j++;
    }
  }
  while (i < n) out.push({ type: "del", text: a[i++] });
  while (j < m) out.push({ type: "add", text: b[j++] });
  return out;
}

/**
 * Suffix-LCS table: `lcs[i][j]` is the length of the longest common subsequence
 * of `a.slice(i)` and `b.slice(j)`, so a forward walk can pick the branch that
 * keeps the most matches ahead of it.
 */
function lcsTable(a: string[], b: string[]): number[][] {
  const n = a.length;
  const m = b.length;
  const lcs: number[][] = Array.from({ length: n + 1 }, () =>
    Array.from({ length: m + 1 }, () => 0),
  );
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    }
  }
  return lcs;
}

/**
 * Second pass over the finished line diff: within each changed block, pair the
 * k-th removed line with the k-th added line and record which words differ
 * between them. Lines with no counterpart (a pure insertion or deletion) are
 * left alone — there is nothing to compare them against, and inventing a partner
 * from a neighbouring line would highlight words that were never edited.
 */
function annotateChangedSegments(lines: DiffLine[]): void {
  let budget = MAX_INTRA_LINE_PAIRS;
  let i = 0;
  while (i < lines.length && budget > 0) {
    if (lines[i].type !== "del") {
      i++;
      continue;
    }
    let addStart = i;
    while (addStart < lines.length && lines[addStart].type === "del") addStart++;
    let blockEnd = addStart;
    while (blockEnd < lines.length && lines[blockEnd].type === "add") blockEnd++;

    const pairs = Math.min(addStart - i, blockEnd - addStart);
    for (let k = 0; k < pairs && budget > 0; k++) {
      budget--;
      const del = lines[i + k];
      const add = lines[addStart + k];
      const segmented = segmentPair(del.text, add.text);
      if (segmented) {
        del.segments = segmented.del;
        add.segments = segmented.add;
      }
    }
    i = Math.max(blockEnd, i + 1);
  }
}

/**
 * Word-level diff of one removed line against its added counterpart, as segments
 * covering each line in full. Returns null when the pair is too big to align
 * under {@link MAX_INTRA_LINE_CELLS} or too dissimilar to be worth splitting
 * ({@link MAX_CHANGED_RATIO}); the caller then leaves both lines unsegmented.
 */
function segmentPair(
  before: string,
  after: string,
): { del: DiffSegment[]; add: DiffSegment[] } | null {
  const a = tokenize(before);
  const b = tokenize(after);

  // Same trim-then-align shape as the line diff: an edit inside a long prompt
  // leaves a handful of tokens in the middle no matter how long the line is.
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }

  const midA = a.slice(start, endA);
  const midB = b.slice(start, endB);
  if (midA.length * midB.length > MAX_INTRA_LINE_CELLS) return null;

  const changedA: boolean[] = Array.from({ length: a.length }, () => false);
  const changedB: boolean[] = Array.from({ length: b.length }, () => false);
  for (let k = 0; k < midA.length; k++) changedA[start + k] = true;
  for (let k = 0; k < midB.length; k++) changedB[start + k] = true;

  // Everything in the middle counts as changed except the tokens the LCS matches
  // up across the two lines.
  const lcs = lcsTable(midA, midB);
  let i = 0;
  let j = 0;
  while (i < midA.length && j < midB.length) {
    if (midA[i] === midB[j]) {
      changedA[start + i] = false;
      changedB[start + j] = false;
      i++;
      j++;
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) {
      i++;
    } else {
      j++;
    }
  }

  if (
    changedRatio(a, changedA, before.length) > MAX_CHANGED_RATIO &&
    changedRatio(b, changedB, after.length) > MAX_CHANGED_RATIO
  ) {
    return null;
  }

  return { del: toSegments(a, changedA), add: toSegments(b, changedB) };
}

function tokenize(text: string): string[] {
  return text.match(TOKEN_PATTERN) ?? [];
}

/** Share of the line's characters that would be emphasised. */
function changedRatio(tokens: string[], changed: boolean[], length: number): number {
  let count = 0;
  for (let i = 0; i < tokens.length; i++) if (changed[i]) count += tokens[i].length;
  return count / Math.max(1, length);
}

/** Collapse per-token flags into the fewest runs that still cover the line. */
function toSegments(tokens: string[], changed: boolean[]): DiffSegment[] {
  const segments: DiffSegment[] = [];
  for (let i = 0; i < tokens.length; i++) {
    const last = segments[segments.length - 1];
    if (last && last.changed === changed[i]) last.text += tokens[i];
    else segments.push({ text: tokens[i], changed: changed[i] });
  }
  return segments;
}

function splitLines(text: string): string[] {
  if (text === "") return [];
  return text.replace(/\n$/, "").split("\n");
}
