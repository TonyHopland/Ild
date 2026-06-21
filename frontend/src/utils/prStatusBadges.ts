import { RemotePrCiStatus, WorkItemPrStatus } from "../types";

/** A single PR status pill: its text and the preview-state tone class to use. */
export interface PrBadge {
  label: string;
  tone: string;
}

const CI_LABELS: Record<RemotePrCiStatus, string> = {
  None: "No CI",
  Pending: "CI running",
  Passed: "CI passed",
  Failed: "CI failed",
};

/** Maps a CI verdict onto the shared preview-state tone classes. */
function ciTone(ci: RemotePrCiStatus): string {
  if (ci === "Passed") return "running";
  if (ci === "Failed") return "error";
  return "stopped";
}

function stateBadge(status: WorkItemPrStatus): PrBadge {
  if (status.merged) return { label: "Merged", tone: "running" };
  if (status.state === "closed") return { label: "Closed", tone: "error" };
  return { label: "Open", tone: "stopped" };
}

/**
 * Derives the PR status pills (state, CI verdict, review decision, mergeability)
 * shown both in the detail dialog's PR view and on the taskboard card. Accepts
 * the badge-relevant {@link WorkItemPrStatus} subset, which a full
 * RemotePrSnapshot structurally satisfies, so both call sites share one source.
 */
export function prStatusBadges(status: WorkItemPrStatus): PrBadge[] {
  const badges: PrBadge[] = [
    stateBadge(status),
    { label: CI_LABELS[status.ci], tone: ciTone(status.ci) },
  ];
  if (status.changesRequested) badges.push({ label: "Changes requested", tone: "error" });
  if (status.approved) badges.push({ label: "Approved", tone: "running" });
  if (status.mergeable === false || status.mergeableState === "dirty")
    badges.push({ label: "Merge conflict", tone: "error" });
  return badges;
}
