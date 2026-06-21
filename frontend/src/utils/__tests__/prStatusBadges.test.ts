import { describe, expect, test } from "vite-plus/test";
import { prStatusBadges } from "../prStatusBadges";
import { WorkItemPrStatus } from "../../types";

function makeStatus(overrides: Partial<WorkItemPrStatus> = {}): WorkItemPrStatus {
  return {
    state: "open",
    merged: false,
    mergeable: true,
    mergeableState: "clean",
    ci: "None",
    approved: false,
    changesRequested: false,
    ...overrides,
  };
}

describe("prStatusBadges", () => {
  test("a clean open PR with no CI shows just the state and CI badges", () => {
    const badges = prStatusBadges(makeStatus());
    expect(badges).toEqual([
      { label: "Open", tone: "stopped" },
      { label: "No CI", tone: "stopped" },
    ]);
  });

  test("maps each CI verdict onto its label and tone", () => {
    expect(prStatusBadges(makeStatus({ ci: "Passed" }))[1]).toEqual({
      label: "CI passed",
      tone: "running",
    });
    expect(prStatusBadges(makeStatus({ ci: "Pending" }))[1]).toEqual({
      label: "CI running",
      tone: "stopped",
    });
    expect(prStatusBadges(makeStatus({ ci: "Failed" }))[1]).toEqual({
      label: "CI failed",
      tone: "error",
    });
  });

  test("appends review and conflict badges only when their states are set", () => {
    const labels = prStatusBadges(
      makeStatus({ ci: "Passed", approved: true, changesRequested: true, mergeable: false }),
    ).map((b) => b.label);
    expect(labels).toEqual([
      "Open",
      "CI passed",
      "Changes requested",
      "Approved",
      "Merge conflict",
    ]);
  });

  test("treats a dirty mergeable-state as a conflict even when mergeable is unknown", () => {
    const labels = prStatusBadges(makeStatus({ mergeable: null, mergeableState: "dirty" })).map(
      (b) => b.label,
    );
    expect(labels).toContain("Merge conflict");
  });

  test("a merged PR shows the Merged state, a closed PR shows Closed", () => {
    expect(prStatusBadges(makeStatus({ merged: true }))[0]).toEqual({
      label: "Merged",
      tone: "running",
    });
    expect(prStatusBadges(makeStatus({ state: "closed" }))[0]).toEqual({
      label: "Closed",
      tone: "error",
    });
  });
});
