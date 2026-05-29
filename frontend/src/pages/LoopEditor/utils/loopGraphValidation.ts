import type { Edge, Node } from "@xyflow/react";
import { NodeType } from "../../../types";

const nodeType = (node: Node): string | undefined => (node.data as { type?: string }).type;

/**
 * Client-side structural checks run before the server-side validate call:
 * a graph needs a Start node, a Cleanup node, every node reachable from Start,
 * and at least one Start→Cleanup path. Returns the list of human-readable
 * errors (empty when the graph passes). Pure — no React or network deps — so
 * it can be unit-tested directly.
 */
export function validateLoopGraphLocally(nodes: Node[], edges: Edge[]): string[] {
  if (nodes.length === 0) {
    return ["Graph must contain at least one node."];
  }

  const errors: string[] = [];
  const types = nodes.map(nodeType);

  if (!types.includes(NodeType.Start)) {
    errors.push("Graph must contain a Start node.");
  }
  if (!types.includes(NodeType.Cleanup)) {
    errors.push("Graph must contain a Cleanup node.");
  }

  const startNode = nodes.find((node) => nodeType(node) === NodeType.Start);
  if (startNode) {
    const adjacency = new Map<string, string[]>();
    for (const edge of edges) {
      const targets = adjacency.get(edge.source) ?? [];
      targets.push(edge.target);
      adjacency.set(edge.source, targets);
    }

    const reachable = new Set<string>([startNode.id]);
    const queue: string[] = [startNode.id];
    while (queue.length > 0) {
      const current = queue.shift()!;
      for (const target of adjacency.get(current) ?? []) {
        if (!reachable.has(target)) {
          reachable.add(target);
          queue.push(target);
        }
      }
    }

    const unreachableNodes = nodes.filter((node) => !reachable.has(node.id)).map((node) => node.id);
    if (unreachableNodes.length > 0) {
      errors.push(`Unreachable nodes from Start: ${unreachableNodes.join(", ")}`);
    }

    const cleanupNodes = nodes.filter((node) => nodeType(node) === NodeType.Cleanup);
    if (cleanupNodes.length > 0 && !cleanupNodes.some((node) => reachable.has(node.id))) {
      errors.push("No path from Start leads to a Cleanup node.");
    }
  }

  return errors;
}
