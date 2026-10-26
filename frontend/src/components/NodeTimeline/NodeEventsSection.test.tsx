import { afterEach, describe, expect, test } from "vite-plus/test";
import { cleanup, render, screen } from "@testing-library/react";
import NodeEventsSection from "./NodeEventsSection";
import { EventLogEntry } from "../../types";

afterEach(cleanup);

function entry(id: number, eventType: string, payload: string): EventLogEntry {
  return {
    id,
    runId: "run-1",
    eventType,
    nodeId: "node-1",
    payload,
    timestamp: "2026-10-26T12:00:00Z",
    runNodeId: "run-node-1",
  };
}

describe("NodeEventsSection", () => {
  test("shows each variable a Set Variable node set, in a badge colour of its own", () => {
    render(
      <NodeEventsSection
        events={[entry(1, "VariableSet", "phase (changed)"), entry(2, "SomethingUnknown", "other")]}
      />,
    );

    expect(screen.getByText("phase (changed)")).toBeTruthy();
    const variableBadge = screen.getByText("VariableSet");
    const fallbackBadge = screen.getByText("SomethingUnknown");
    expect(variableBadge.style.color).not.toBe(fallbackBadge.style.color);
  });
});
