/**
 * A minimal line-level diff, enough to drive the save-time review modal in the
 * Loop Editor (ADR-0011). The repo carries no diff library and the files-tab
 * DiffView takes an already-computed unified-diff string, so we compute our own
 * line diff between the last-saved and currently-edited loop JSON here.
 */

export type DiffLineType = "context" | "add" | "del";

export interface DiffLine {
  type: DiffLineType;
  text: string;
}

/**
 * Longest-common-subsequence line diff between two texts. Emits removed lines
 * (from `before`), added lines (from `after`), and unchanged context lines in
 * source order. Trailing newlines are ignored so an identical document diffs to
 * all-context with no spurious blank line.
 */
export function computeLineDiff(before: string, after: string): DiffLine[] {
  const a = splitLines(before);
  const b = splitLines(after);

  // Classic LCS table over lines.
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

/** True when the two texts are line-for-line identical (trailing newline aside). */
export function hasChanges(before: string, after: string): boolean {
  return computeLineDiff(before, after).some((line) => line.type !== "context");
}

function splitLines(text: string): string[] {
  if (text === "") return [];
  return text.replace(/\n$/, "").split("\n");
}
