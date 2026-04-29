import type { Node, Edge } from "@xyflow/react";
import type { LoopTemplate } from "../types";
import { EdgeType } from "../types";

function layoutPosition(index: number): { x: number; y: number } {
  const columns = 2;
  const col = index % columns;
  const row = Math.floor(index / columns);
  return {
    x: 100 + col * 260,
    y: 80 + row * 140,
  };
}

export function templateToNodes(template: LoopTemplate): Node[] {
  return template.nodes.map((node, index) => {
    const pos = layoutPosition(index);
    return {
      id: node.id,
      type: "loopNode",
      position: pos,
      data: {
        label: node.label,
        type: node.type,
      },
    };
  });
}

export function templateToEdges(template: LoopTemplate): Edge[] {
  return template.edges.map((edge) => ({
    id: edge.id,
    source: edge.sourceNodeId,
    target: edge.targetNodeId,
    animated: edge.edgeType === EdgeType.OnSuccess,
    style: {
      stroke: edge.edgeType === EdgeType.OnSuccess ? "#10b981" : "#ef4444",
      strokeDasharray: edge.edgeType === EdgeType.OnFailure ? "8 4" : undefined,
    },
    label: edge.edgeType === EdgeType.OnSuccess ? "success" : "failure",
    labelStyle: { fill: "#a0a0b0", fontSize: "0.7rem" },
    labelBgStyle: { fill: "#1e1e30" },
    labelBgPadding: [4, 2] as [number, number],
    labelBgBorderRadius: 4,
  }));
}
