import { WorktreeCommits, WorktreeDiffRange } from "../types";

/**
 * What the Files tab's range dropdown has ticked: everything the branch
 * changed, or one unbroken run of [Pending, newest commit … oldest commit] —
 * Pending standing for what is not committed yet, as the newest item of all.
 * `commits` are full SHAs, newest first.
 */
export type DiffSelection = { kind: "all" } | { kind: "some"; pending: boolean; commits: string[] };

export const ALL_CHANGES: DiffSelection = { kind: "all" };

/** The dropdown's item for what is not committed yet; no commit SHA can collide with it. */
export const PENDING = "pending";

/** Whether the list can back a narrower range: it loaded, and the server resolved a base for it. */
export function isCommitListAvailable(
  list: WorktreeCommits | null,
): list is WorktreeCommits & { baseSha: string } {
  return list !== null && list.baseSha !== null;
}

/** Every item a run can be made of, in run order. */
function itemsOf(list: WorktreeCommits): string[] {
  return [PENDING, ...list.commits.map((c) => c.sha)];
}

function isTicked(selection: DiffSelection, item: string): boolean {
  if (selection.kind === "all") return false;
  return item === PENDING ? selection.pending : selection.commits.includes(item);
}

/** The ticked items, in run order — the run itself, for a selection that is one. */
function tickedItems(selection: DiffSelection, list: WorktreeCommits): string[] {
  return itemsOf(list).filter((item) => isTicked(selection, item));
}

/** The shortest run holding every one of `wanted`; All when there are none. */
function run(list: WorktreeCommits, wanted: string[]): DiffSelection {
  const items = itemsOf(list);
  const positions = wanted.map((item) => items.indexOf(item)).filter((i) => i >= 0);
  if (positions.length === 0) return ALL_CHANGES;
  const span = items.slice(Math.min(...positions), Math.max(...positions) + 1);
  return {
    kind: "some",
    pending: span[0] === PENDING,
    commits: span.filter((item) => item !== PENDING),
  };
}

function sameSelection(a: DiffSelection, b: DiffSelection): boolean {
  if (a.kind === "all" || b.kind === "all") return a.kind === b.kind;
  return (
    a.pending === b.pending &&
    a.commits.length === b.commits.length &&
    a.commits.every((sha, i) => sha === b.commits[i])
  );
}

/**
 * The selection after clicking Pending or a commit. Ticking one stretches the
 * run to reach it; unticking one of its ends shortens it, and unticking the
 * last returns to All.
 */
export function toggleDiffSelection(
  selection: DiffSelection,
  item: string,
  list: WorktreeCommits,
): DiffSelection {
  const ticked = tickedItems(selection, list);
  return run(list, ticked.includes(item) ? ticked.filter((i) => i !== item) : [...ticked, item]);
}

/** Whether `item` can be clicked: never without a list, and never inside the run, which it would split. */
export function canToggle(
  selection: DiffSelection,
  item: string,
  list: WorktreeCommits | null,
): boolean {
  if (!isCommitListAvailable(list)) return false;
  const ticked = tickedItems(selection, list);
  const at = ticked.indexOf(item);
  return at <= 0 || at === ticked.length - 1;
}

/**
 * The selection carried onto a freshly loaded list: commits no longer on the
 * branch drop out, a commit landing inside the run joins it, and nothing left
 * ticked — or no usable list — is All. The same object comes back when nothing
 * changed, so a refresh that changes nothing re-renders nothing.
 */
export function reconcileDiffSelection(
  selection: DiffSelection,
  list: WorktreeCommits | null,
): DiffSelection {
  if (selection.kind === "all") return selection;
  if (!isCommitListAvailable(list)) return ALL_CHANGES;
  const next = run(list, tickedItems(selection, list));
  return sameSelection(next, selection) ? selection : next;
}

/**
 * The range the server is asked for, or undefined for All. The run's oldest
 * commit is measured from its parent; it ends at its newest commit, or at the
 * working tree when Pending is in it. Pending alone starts at the branch's
 * newest commit — the base, when it has none.
 */
export function diffRangeOf(
  selection: DiffSelection,
  list: WorktreeCommits | null,
): WorktreeDiffRange | undefined {
  if (selection.kind === "all" || !isCommitListAvailable(list)) return undefined;
  const chosen = list.commits.filter((c) => selection.commits.includes(c.sha));
  if (chosen.length === 0) {
    return { from: list.commits.length > 0 ? list.commits[0].sha : list.baseSha };
  }
  const oldest = chosen[chosen.length - 1];
  return selection.pending
    ? { from: oldest.parentSha }
    : { from: oldest.parentSha, to: chosen[0].sha };
}

/** One string per range, "" for All, so a change of range can be told from a new object. */
export function diffRangeKey(range: WorktreeDiffRange | undefined): string {
  return range ? `${range.from ?? ""}..${range.to ?? ""}` : "";
}

/** What the closed dropdown says it shows. */
export function diffSelectionLabel(selection: DiffSelection): string {
  if (selection.kind === "all") return "All changes";
  const count = selection.commits.length;
  if (count === 0) return "Pending changes";
  const commits = `${count} commit${count === 1 ? "" : "s"}`;
  return selection.pending ? `${commits} + pending` : commits;
}
