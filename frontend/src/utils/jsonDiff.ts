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
 * Line diff between two texts. Emits removed lines (from `before`), added lines
 * (from `after`), and unchanged context lines in source order. Trailing newlines
 * are ignored so an identical document diffs to all-context with no spurious
 * blank line. Bounded in time and memory: common prefix/suffix are trimmed in
 * O(n+m), and the differing middle is diffed with LCS only while it stays under
 * {@link MAX_LCS_CELLS}, degrading to a block replace for pathologically large
 * inputs.
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
  return out;
}

/** LCS-backed diff of the already-trimmed differing region. */
function diffMiddle(a: string[], b: string[]): DiffLine[] {
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

function splitLines(text: string): string[] {
  if (text === "") return [];
  return text.replace(/\n$/, "").split("\n");
}
