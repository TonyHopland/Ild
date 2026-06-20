import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import { PrView } from "./panels";
import type { RemotePrSnapshot } from "../../types";

afterEach(() => cleanup());

function snapshot(overrides: Partial<RemotePrSnapshot> = {}): RemotePrSnapshot {
  return {
    title: "Add widget",
    body: "Implements the widget.",
    state: "open",
    merged: false,
    mergeable: true,
    mergeableState: "clean",
    ci: "Passed",
    approved: true,
    changesRequested: false,
    conversation: [
      {
        kind: "review",
        author: "alice",
        body: "looks good",
        createdAt: "2026-01-01T00:00:00Z",
        state: "APPROVED",
      },
      {
        kind: "comment",
        author: "bob",
        body: "thanks",
        createdAt: "2026-01-02T00:00:00Z",
        state: null,
      },
    ],
    fetchedAt: "2026-01-02T00:00:00Z",
    ...overrides,
  };
}

describe("PrView", () => {
  test("renders title, CI/approval badges, description and conversation", () => {
    render(<PrView snapshot={snapshot()} />);

    expect(screen.getByText("Add widget")).toBeTruthy();
    expect(screen.getByText("CI passed")).toBeTruthy();
    expect(screen.getByText("Approved")).toBeTruthy();
    expect(screen.getByText("Open")).toBeTruthy();
    expect(screen.getByText("Implements the widget.")).toBeTruthy();
    expect(screen.getByText("alice")).toBeTruthy();
    expect(screen.getByText("looks good")).toBeTruthy();
    expect(screen.getByText("bob")).toBeTruthy();
  });

  test("shows merge-conflict and changes-requested badges and a merged state", () => {
    render(
      <PrView
        snapshot={snapshot({
          state: "open",
          ci: "Failed",
          approved: false,
          changesRequested: true,
          mergeable: false,
          mergeableState: "dirty",
        })}
      />,
    );

    expect(screen.getByText("CI failed")).toBeTruthy();
    expect(screen.getByText("Changes requested")).toBeTruthy();
    expect(screen.getByText("Merge conflict")).toBeTruthy();
  });
});
