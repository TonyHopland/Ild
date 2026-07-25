import {
  computeWordDiff,
  createWordDiffBudget,
  DiffSegment,
  isWordDiffBudgetSpent,
} from "./jsonDiff";

/**
 * How a diff row is shaded. The members double as CSS class suffixes at the
 * render site (`wiv2-diff-${kind}`, `wiv2-diff-seg-${kind}`), so renaming one
 * silently restyles the viewer — the coupling is deliberate, to keep one
 * vocabulary rather than a classifier that hands back whole class names.
 */
export type DiffRowKind = "hunk" | "add" | "del" | "ctx";

/** One row of a unified diff, classified and optionally broken into words. */
export interface DiffRow {
  /** The raw diff line, marker included — what renders when `segments` is absent. */
  text: string;
  kind: DiffRowKind;
  /**
   * Word-level runs of the line's payload (`text` past its marker), present only
   * on a del/add line {@link parseUnifiedDiff} could pair with a counterpart.
   * They cover the payload in full, so the renderer can shade every run. Absent
   * for an unpaired line or a pair {@link computeWordDiff} declined — both
   * ordinary, and both leave the line to the whole-line treatment.
   */
  segments?: DiffSegment[];
}

/**
 * Parse a git unified diff into rows a viewer can shade in two tiers: each line
 * classified for its light tier, and the del/add runs within each hunk paired up
 * so the words that actually differ can carry a strong tier on top.
 *
 * Pairing is index-wise: the k-th removed line of a run against the k-th added
 * line of the run following it, up to the shorter of the two. Lines past that
 * have no counterpart to compare against, and borrowing one from a neighbour
 * would emphasise words that were never edited. A pair {@link computeWordDiff}
 * declines — too dissimilar to be worth splitting, or too expensive under the
 * caps below — simply keeps no segments.
 *
 * Assumes a single-file patch, which is what this viewer is fed: `git diff
 * <base> -- <path>`. A multi-file patch would let a second file's `--- a/x`
 * arrive with a hunk still open and pair as content.
 */
export function parseUnifiedDiff(diff: string): DiffRow[] {
  const rows: DiffRow[] = diff
    .replace(/\n$/, "")
    .split("\n")
    .map((text) => ({ text, kind: rowKind(text) }));

  // One allowance for the whole patch, spent the way the save-diff path spends
  // it. This placement is the load-bearing part: the caps exist to keep the
  // quadratic word alignment off the critical path, and creating the budget per
  // pair instead of per patch would turn a 1M-cell allowance into a 50M one and
  // stop the paired-line cap binding at all.
  const budget = createWordDiffBudget();

  // Only lines inside a hunk are content. The patch preamble's "--- a/x" and
  // "+++ b/x" are prefixed like a del/add pair (and shade like one, longstanding
  // behaviour left alone here), but diffing those two paths against each other
  // would emphasise nothing a reader cares about. The discriminator has to be
  // "have we reached an @@ yet" rather than the prefix, because inside a hunk
  // "---" is genuine content: a deleted line reading "-- note" arrives with its
  // marker as "--- note".
  // Classification is already done, so once the budget is spent there is nothing
  // left for this walk to attach and it can stop where it stands.
  let inHunk = false;
  let i = 0;
  while (i < rows.length && !isWordDiffBudgetSpent(budget)) {
    if (rows[i].kind === "hunk") {
      inHunk = true;
      i++;
    } else if (!inHunk || rows[i].kind !== "del") {
      i++;
    } else {
      const dels = collectRun(rows, i, "del");
      const adds = collectRun(rows, dels.end, "add");
      const pairs = Math.min(dels.indices.length, adds.indices.length);
      for (let k = 0; k < pairs && !isWordDiffBudgetSpent(budget); k++) {
        const del = rows[dels.indices[k]];
        const add = rows[adds.indices[k]];
        const words = computeWordDiff(del.text.slice(1), add.text.slice(1), budget);
        if (words) {
          del.segments = words.del;
          add.segments = words.add;
        }
      }
      // The del run is never empty here, so this always advances.
      i = adds.end;
    }
  }

  return rows;
}

/**
 * The consecutive `kind` lines starting at `from`, and where that run ends.
 *
 * A "\ No newline at end of file" marker is stepped over rather than ending the
 * run: it annotates the line before it, and in a file with no trailing newline
 * git puts one squarely between the removed and added sides of the pair most
 * worth segmenting. Inside a hunk a leading backslash can only be that marker —
 * real content always carries a +, - or space marker first.
 */
function collectRun(
  rows: DiffRow[],
  from: number,
  kind: DiffRowKind,
): { indices: number[]; end: number } {
  const indices: number[] = [];
  let i = from;
  while (i < rows.length) {
    if (rows[i].kind === kind) indices.push(i);
    else if (!rows[i].text.startsWith("\\")) break;
    i++;
  }
  return { indices, end: i };
}

function rowKind(line: string): DiffRowKind {
  if (line.startsWith("@@")) return "hunk";
  if (line.startsWith("+")) return "add";
  if (line.startsWith("-")) return "del";
  return "ctx";
}
