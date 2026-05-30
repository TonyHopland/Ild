import type { Edge, Node } from "@xyflow/react";
import { describe, expect, test } from "vite-plus/test";
import { NodeType } from "../../../types";
import { validateLoopGraphLocally } from "./loopGraphValidation";

const node = (id: string, type: NodeType): Node => ({
  id,
  position: { x: 0, y: 0 },
  data: { type },
});

const edge = (source: string, target: string): Edge => ({
  id: `${source}-${target}`,
  source,
  target,
});

describe("validateLoopGraphLocally", () => {
  test("requires at least one node", () => {
    expect(validateLoopGraphLocally([], [])).toEqual(["Graph must contain at least one node."]);
  });

  test("requires Start and Cleanup nodes", () => {
    const errors = validateLoopGraphLocally([node("a", NodeType.Cmd)], []);
    expect(errors).toContain("Graph must contain a Start node.");
    expect(errors).toContain("Graph must contain a Cleanup node.");
  });

  test("flags nodes unreachable from Start", () => {
    const nodes = [
      node("s", NodeType.Start),
      node("c", NodeType.Cleanup),
      node("orphan", NodeType.Cmd),
    ];
    const errors = validateLoopGraphLocally(nodes, [edge("s", "c")]);
    expect(errors).toContain("Unreachable nodes from Start: orphan");
  });

  test("flags when no Start→Cleanup path exists", () => {
    const nodes = [node("s", NodeType.Start), node("c", NodeType.Cleanup)];
    // Cleanup is its own island (no edge from Start), so it's both unreachable
    // and unreached — the no-path error must be present.
    const errors = validateLoopGraphLocally(nodes, []);
    expect(errors).toContain("No path from Start leads to a Cleanup node.");
  });

  test("passes a well-formed Start→Cleanup graph", () => {
    const nodes = [node("s", NodeType.Start), node("ai", NodeType.AI), node("c", NodeType.Cleanup)];
    const edges = [edge("s", "ai"), edge("ai", "c")];
    expect(validateLoopGraphLocally(nodes, edges)).toEqual([]);
  });
});
