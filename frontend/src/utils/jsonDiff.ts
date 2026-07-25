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
 * Cap on the LCS table size, counted as {@link lcsCells} actually allocates it. A
 * loop document can be up to ~1 MB on the server, which pretty-prints to tens of
 * thousands of lines; an unbounded O(n·m) table over that would allocate
 * gigabytes and freeze the tab. We first strip the common prefix/suffix — which
 * collapses the usual case (a localized edit) to a tiny changed middle — and only
 * run the quadratic LCS when the remaining middle fits under this cap; otherwise
 * we degrade to a coarse block-replace so the modal still opens instantly.
 */
const MAX_LCS_CELLS = 1_000_000;

/**
 * Same bounded-cost discipline for the intra-line pass, which runs a second LCS
 * per del/add pair. Three separate limits, because they bound different costs:
 *
 * - {@link MAX_INTRA_LINE_TOTAL_CELLS} is one allowance for the whole document,
 *   so the quadratic work does *not* multiply out to pairs × per-pair cells. It
 *   matches {@link MAX_LCS_CELLS}, which keeps the pass in the same class as the
 *   line diff that feeds it: measured here, a full-size line table costs ~24 ms
 *   and a document that spends this entire budget adds ~27 ms — a modal that
 *   opens a frame later in the worst case, not one that stalls.
 * - {@link MAX_INTRA_LINE_CELLS} keeps a single pathological pair from eating
 *   the whole allowance and starving the rest of the document.
 * - {@link MAX_INTRA_LINE_PAIRS} bounds the linear per-pair work — tokenizing
 *   two lines — for a document made of thousands of individually cheap pairs.
 *
 * Exceeding any of them is not an error: the pair simply keeps no segments and
 * renders as a plain changed line.
 *
 * All three are spent through a single {@link WordDiffBudget} that
 * {@link computeWordDiff} debits itself, so every caller pairing up a document's
 * worth of lines gets the same discipline without restating it.
 */
const MAX_INTRA_LINE_CELLS = 250_000;
const MAX_INTRA_LINE_TOTAL_CELLS = 1_000_000;
const MAX_INTRA_LINE_PAIRS = 200;

/**
 * A del/add pair where this much of *both* lines is rewritten isn't one line
 * edited — it's a wholesale replacement, or two unrelated lines that happen to
 * be adjacent (the usual source being the block-replace fallback above).
 * Emphasising nearly every word says no more than shading the line does, so
 * those keep the plain whole-line treatment. See {@link tooDissimilar} for what
 * "this much" counts.
 *
 * Calibrated against the pairs in the "worth segmenting" test table rather than
 * picked: over realistic loop-JSON lines, everything that should be segmented
 * scores at or under 0.45 and everything that should not scores 0.65 or more, so
 * this sits in the middle of that gap.
 */
const MAX_REWRITTEN_SHARE = 0.55;

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
  if (lcsCells(midA, midB) > MAX_LCS_CELLS) {
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
 * What {@link lcsTable} will allocate for these two sequences, sentinel row and
 * column included. Every cap in this file is spent through here so a guard can
 * never drift from the table it is guarding: the sentinels are not a rounding
 * error for lopsided pairs, where one line against a thousand costs two rows of
 * a thousand rather than the thousand cells `n × m` would suggest.
 */
function lcsCells(a: string[], b: string[]): number {
  return (a.length + 1) * (b.length + 1);
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
  // One allowance for the whole document: the quadratic work every pair does is
  // drawn from the same pot, so a document full of expensive pairs costs no more
  // than a single expensive one.
  const budget = createWordDiffBudget();
  let i = 0;
  while (i < lines.length) {
    if (lines[i].type !== "del") {
      i++;
      continue;
    }
    let addStart = i;
    while (addStart < lines.length && lines[addStart].type === "del") addStart++;
    let blockEnd = addStart;
    while (blockEnd < lines.length && lines[blockEnd].type === "add") blockEnd++;

    const pairs = Math.min(addStart - i, blockEnd - addStart);
    for (let k = 0; k < pairs; k++) {
      const del = lines[i + k];
      const add = lines[addStart + k];
      const words = computeWordDiff(del.text, add.text, budget);
      if (words) {
        del.segments = words.del;
        add.segments = words.add;
      }
    }
    i = Math.max(blockEnd, i + 1);
  }
}

/** The two sides of a word-level diff, each covering its line in full. */
export interface WordDiff {
  del: DiffSegment[];
  add: DiffSegment[];
}

/**
 * What one document's intra-line pass is allowed to spend, drawn down by each
 * {@link computeWordDiff} call it is passed to. Both fields are aggregate, not
 * per pair, which is the whole point: pass one object to every pair of a
 * document — never a fresh one per pair — and the quadratic work cannot multiply
 * out no matter how many changed lines that document has.
 */
export interface WordDiffBudget {
  /** Alignment cells left to spend ({@link MAX_INTRA_LINE_TOTAL_CELLS}). */
  cells: number;
  /** del/add pairs left to tokenize ({@link MAX_INTRA_LINE_PAIRS}). */
  pairs: number;
}

/** A full allowance, for one document about to be rendered. */
export function createWordDiffBudget(): WordDiffBudget {
  return { cells: MAX_INTRA_LINE_TOTAL_CELLS, pairs: MAX_INTRA_LINE_PAIRS };
}

/**
 * Word-level diff of one removed line against the added line that replaced it —
 * the pass that turns "this line changed" into "these words changed", and what
 * {@link computeLineDiff} uses to fill {@link DiffLine.segments}.
 *
 * Returns null when the pair is not worth segmenting, leaving both lines to the
 * whole-line treatment: when the document's `budget` is out of pairs, when the
 * alignment table would exceed {@link MAX_INTRA_LINE_CELLS} or the cells that
 * budget has left, or when the two lines are {@link tooDissimilar}. Omitting
 * `budget` gives this pair an allowance of its own, which is right for a one-off
 * comparison and wrong for a document — see {@link WordDiffBudget}.
 */
export function computeWordDiff(
  before: string,
  after: string,
  budget: WordDiffBudget = createWordDiffBudget(),
): WordDiff | null {
  // Charged before the tokenize below, since bounding that linear work for a
  // document made of thousands of cheap pairs is what this cap is for.
  if (budget.pairs <= 0) return null;
  budget.pairs--;

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
  const cells = lcsCells(midA, midB);
  if (cells > MAX_INTRA_LINE_CELLS || cells > budget.cells) return null;
  budget.cells -= cells;

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

  const del: AlignedLine = { tokens: a, changed: changedA };
  const add: AlignedLine = { tokens: b, changed: changedB };
  if (tooDissimilar(del, add)) return null;

  return { del: toSegments(del), add: toSegments(add) };
}

/** One line of a pair, tokenized, with each token's fate after alignment. */
interface AlignedLine {
  tokens: string[];
  changed: boolean[];
}

function tokenize(text: string): string[] {
  return text.match(TOKEN_PATTERN) ?? [];
}

/**
 * Would segmenting this pair emphasise so much of *both* lines that the strong
 * tier stops carrying information? Only both, because a line that grew by a
 * clause is entirely unchanged on the removed side and still worth segmenting.
 */
function tooDissimilar(del: AlignedLine, add: AlignedLine): boolean {
  return rewrittenShare(del) > MAX_REWRITTEN_SHARE && rewrittenShare(add) > MAX_REWRITTEN_SHARE;
}

/**
 * Share of a line's *substance* — its non-whitespace characters — left without a
 * counterpart in the other line.
 *
 * Whitespace is excluded from the count on purpose (it still takes part in the
 * alignment, so a word that merely shifted can still match): prose runs about
 * one character in six as spaces, and the LCS pairs space to space almost
 * unconditionally, so counting them scores two entirely unrelated prompts as a
 * sixth alike before a single word has lined up. Matched JSON boilerplate does
 * count as substance and so does buy real similarity — a key and its quoting are
 * genuinely shared text — which is why the threshold sits well below 1.
 */
function rewrittenShare(line: AlignedLine): number {
  let rewritten = 0;
  let total = 0;
  for (let i = 0; i < line.tokens.length; i++) {
    const token = line.tokens[i];
    if (isWhitespace(token)) continue;
    total += token.length;
    if (line.changed[i]) rewritten += token.length;
  }
  return total === 0 ? 0 : rewritten / total;
}

function isWhitespace(token: string): boolean {
  return /^\s/.test(token);
}

/** Collapse per-token flags into the fewest runs that still cover the line. */
function toSegments(line: AlignedLine): DiffSegment[] {
  const segments: DiffSegment[] = [];
  for (let i = 0; i < line.tokens.length; i++) {
    const last = segments[segments.length - 1];
    if (last && last.changed === line.changed[i]) last.text += line.tokens[i];
    else segments.push({ text: line.tokens[i], changed: line.changed[i] });
  }
  return segments;
}

function splitLines(text: string): string[] {
  if (text === "") return [];
  return text.replace(/\n$/, "").split("\n");
}
