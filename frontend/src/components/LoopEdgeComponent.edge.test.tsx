import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import type { Edge, EdgeProps } from "@xyflow/react";

// React Flow's full canvas can't mount under jsdom (it needs DOMMatrixReadOnly),
// so stub the primitives the edge component uses. The store is seeded with the
// two parallel edges below so parallelEdgeRoute (the real helper) sees them as
// siblings, and getEdge echoes back an edge by id so a label click resolves to
// its own edge.
const storeEdges = [
  {
    id: "e-ci",
    source: "pr",
    target: "fix",
    sourceHandle: "respond",
    targetHandle: "target-handle",
  },
  {
    id: "e-reject",
    source: "pr",
    target: "fix",
    sourceHandle: "respond",
    targetHandle: "target-handle",
  },
];

vi.mock("@xyflow/react", () => ({
  BaseEdge: ({ id, path }: { id: string; path: string }) => (
    <path data-testid={`edge-path-${id}`} d={path} />
  ),
  EdgeLabelRenderer: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
  getSmoothStepPath: () => ["M 0,0 L 1,1", 0, 0],
  useReactFlow: () => ({
    getEdge: (id: string) => ({ id, source: "pr", target: "fix", data: {} }) as Edge,
  }),
  useStore: (selector: (store: { edges: typeof storeEdges }) => unknown) =>
    selector({ edges: storeEdges }),
}));

const { default: LoopEdgeComponent } = await import("./LoopEdgeComponent");
const { LoopEdgeInteractionContext } = await import("./loopEdgeInteraction");

function edgeProps(id: string, label: string): EdgeProps {
  return {
    id,
    sourceX: 0,
    sourceY: 0,
    targetX: 200,
    targetY: 0,
    sourcePosition: "top",
    targetPosition: "left",
    label,
  } as unknown as EdgeProps;
}

function renderEdge(id: string, label: string, onSelect: (edge: Edge) => void) {
  return render(
    <LoopEdgeInteractionContext.Provider value={onSelect}>
      <svg>
        <LoopEdgeComponent {...edgeProps(id, label)} />
      </svg>
    </LoopEdgeInteractionContext.Provider>,
  );
}

afterEach(() => cleanup());

describe("LoopEdgeComponent label selection", () => {
  test("clicking a label selects that label's own edge, not whichever line is on top", () => {
    const onSelect = vi.fn();
    // Both parallel siblings are on screen at once, as in the editor.
    renderEdge("e-ci", "ci_failure", onSelect);
    renderEdge("e-reject", "reject", onSelect);

    fireEvent.click(screen.getByText("reject"));
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect.mock.calls[0][0].id).toBe("e-reject");

    fireEvent.click(screen.getByText("ci_failure"));
    expect(onSelect).toHaveBeenCalledTimes(2);
    expect(onSelect.mock.calls[1][0].id).toBe("e-ci");
  });

  test("the label is an interactive control with pointer events enabled", () => {
    renderEdge("e-ci", "ci_failure", vi.fn());
    const label = screen.getByText("ci_failure");
    expect(label.getAttribute("role")).toBe("button");
    expect((label as HTMLElement).style.pointerEvents).toBe("all");
    expect((label as HTMLElement).style.cursor).toBe("pointer");
  });

  test("parallel siblings each render their own shifted track, not a shared path", () => {
    renderEdge("e-ci", "ci_failure", vi.fn());
    renderEdge("e-reject", "reject", vi.fn());

    const ciPath = screen.getByTestId("edge-path-e-ci").getAttribute("d");
    const rejectPath = screen.getByTestId("edge-path-e-reject").getAttribute("d");
    // Two siblings → the parallel-lane path is used (a straight "L" track), and
    // the two tracks differ because each rides its own perpendicular offset.
    expect(ciPath).toContain(" L ");
    expect(rejectPath).toContain(" L ");
    expect(ciPath).not.toBe(rejectPath);
  });
});
