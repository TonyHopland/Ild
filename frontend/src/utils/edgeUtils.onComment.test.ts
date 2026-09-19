import { describe, expect, test } from "vite-plus/test";
import type { Node } from "@xyflow/react";
import { getCustomEdgeNames, PR_RESERVED_EDGE_NAMES } from "./edgeUtils";
import { NodeType } from "../types";

function node(id: string, type: NodeType, config: Record<string, unknown> = {}): Node {
  return { id, position: { x: 0, y: 0 }, data: { type, config } } as Node;
}

// The reserved list mirrors ILD.Core's PrNodeEdges, priority order included: an
// editor that cannot offer on_comment cannot wire it, and a list in the wrong
// order stops telling an author which state wins a tick.
describe("the reserved PR edge for review comments", () => {
  test("sits below on_ci_failed and above on_approved, with the other seven unmoved", () => {
    expect([...PR_RESERVED_EDGE_NAMES]).toEqual([
      "on_rejected",
      "on_merge_conflict",
      "on_ci_failed",
      "on_comment",
      "on_approved",
      "on_ci_passed",
      "on_merged",
      "on_abandoned",
    ]);
  });

  test("is offered on a PR node alongside the edges it declares itself", () => {
    const names = getCustomEdgeNames(node("p", NodeType.PR, { customEdges: ["custom_extra"] }));

    expect(names).toContain("on_comment");
    expect(names).toContain("custom_extra");
  });

  test("is not offered on nodes that never park at a pull request", () => {
    expect(
      getCustomEdgeNames(node("h", NodeType.Human, { customEdges: ["Respond"] })),
    ).not.toContain("on_comment");
  });
});
