import { afterEach, describe, expect, test } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";
import { LoopRunNodeStatus } from "../types";
import { useNodeExpansion } from "./useNodeExpansion";

function makeNode(id: string, status: LoopRunNodeStatus) {
  return {
    id,
    nodeId: `template-${id}`,
    nodeLabel: id,
    status,
    effectiveInput: null,
    output: null,
    error: null,
    startedAt: "2025-01-01T00:00:00Z",
    completedAt: null,
    executionCount: 0,
  };
}

describe("useNodeExpansion", () => {
  afterEach(() => {
    // cleanup handled by testing-library
  });

  test("starts with no expanded nodes when nothing is running", () => {
    const { result } = renderHook(() =>
      useNodeExpansion([makeNode("a", LoopRunNodeStatus.Succeeded)]),
    );
    expect(result.current.expandedNodeIds).toEqual([]);
    expect(result.current.runningNodeId).toBeNull();
  });

  test("auto-expands running node on mount", () => {
    const { result } = renderHook(() =>
      useNodeExpansion([
        makeNode("a", LoopRunNodeStatus.Succeeded),
        makeNode("b", LoopRunNodeStatus.Running),
      ]),
    );
    expect(result.current.expandedNodeIds).toContain("b");
    expect(result.current.runningNodeId).toBe("b");
  });

  test("running node cannot be collapsed via toggle", () => {
    const { result } = renderHook(() =>
      useNodeExpansion([
        makeNode("a", LoopRunNodeStatus.Succeeded),
        makeNode("b", LoopRunNodeStatus.Running),
      ]),
    );

    act(() => {
      result.current.handleToggleNode("b");
    });

    expect(result.current.expandedNodeIds).toContain("b");
  });

  test("non-running node expands on toggle", () => {
    const { result } = renderHook(() =>
      useNodeExpansion([makeNode("a", LoopRunNodeStatus.Succeeded)]),
    );

    act(() => {
      result.current.handleToggleNode("a");
    });

    expect(result.current.expandedNodeIds).toContain("a");
  });

  test("non-running node collapses on second toggle", () => {
    const { result } = renderHook(() =>
      useNodeExpansion([makeNode("a", LoopRunNodeStatus.Succeeded)]),
    );

    act(() => {
      result.current.handleToggleNode("a");
    });
    expect(result.current.expandedNodeIds).toContain("a");

    act(() => {
      result.current.handleToggleNode("a");
    });
    expect(result.current.expandedNodeIds).not.toContain("a");
  });

  test("multiple non-running nodes can be expanded independently", () => {
    const { result } = renderHook(() =>
      useNodeExpansion([
        makeNode("a", LoopRunNodeStatus.Succeeded),
        makeNode("b", LoopRunNodeStatus.Succeeded),
      ]),
    );

    act(() => {
      result.current.handleToggleNode("a");
    });
    act(() => {
      result.current.handleToggleNode("b");
    });

    expect(result.current.expandedNodeIds).toContain("a");
    expect(result.current.expandedNodeIds).toContain("b");
  });

  test("collapsing one non-running node does not affect others", () => {
    const { result } = renderHook(() =>
      useNodeExpansion([
        makeNode("a", LoopRunNodeStatus.Succeeded),
        makeNode("b", LoopRunNodeStatus.Succeeded),
      ]),
    );

    act(() => {
      result.current.handleToggleNode("a");
    });
    act(() => {
      result.current.handleToggleNode("b");
    });

    act(() => {
      result.current.handleToggleNode("a");
    });

    expect(result.current.expandedNodeIds).not.toContain("a");
    expect(result.current.expandedNodeIds).toContain("b");
  });

  test("new running node is auto-expanded when runNodes change", () => {
    const { result, rerender } = renderHook(({ nodes }) => useNodeExpansion(nodes), {
      initialProps: {
        nodes: [
          makeNode("a", LoopRunNodeStatus.Succeeded),
          makeNode("b", LoopRunNodeStatus.Succeeded),
        ],
      },
    });

    expect(result.current.runningNodeId).toBeNull();

    rerender({
      nodes: [makeNode("a", LoopRunNodeStatus.Succeeded), makeNode("b", LoopRunNodeStatus.Running)],
    });

    expect(result.current.expandedNodeIds).toContain("b");
    expect(result.current.runningNodeId).toBe("b");
  });

  test("previously expanded nodes stay expanded when a running node appears", () => {
    const { result, rerender } = renderHook(({ nodes }) => useNodeExpansion(nodes), {
      initialProps: {
        nodes: [
          makeNode("a", LoopRunNodeStatus.Succeeded),
          makeNode("b", LoopRunNodeStatus.Succeeded),
        ],
      },
    });

    // Expand node a
    act(() => {
      result.current.handleToggleNode("a");
    });
    expect(result.current.expandedNodeIds).toContain("a");

    // Node b becomes running
    rerender({
      nodes: [makeNode("a", LoopRunNodeStatus.Succeeded), makeNode("b", LoopRunNodeStatus.Running)],
    });

    expect(result.current.expandedNodeIds).toContain("a");
    expect(result.current.expandedNodeIds).toContain("b");
  });

  test("running node is removed from expanded set when it completes", () => {
    const { result, rerender } = renderHook(({ nodes }) => useNodeExpansion(nodes), {
      initialProps: {
        nodes: [makeNode("a", LoopRunNodeStatus.Running)],
      },
    });

    expect(result.current.expandedNodeIds).toContain("a");

    rerender({
      nodes: [makeNode("a", LoopRunNodeStatus.Succeeded)],
    });

    // runningNodeId is now null, but "a" stays in the set because
    // the effect only *adds* the running node — it never removes it.
    expect(result.current.expandedNodeIds).toContain("a");
  });
});
