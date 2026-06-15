import type { Node, Edge } from "@xyflow/react";
import type { LoopTemplate, LoopNodeEdge, LoopNode } from "../types";
import { EdgeType, NodeType } from "../types";

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
    const saved = (node.config as { __pos?: { x: number; y: number } } | undefined)?.__pos;
    const pos =
      saved && typeof saved.x === "number" && typeof saved.y === "number"
        ? { x: saved.x, y: saved.y }
        : layoutPosition(index);
    return {
      id: node.id,
      type: "loopNode",
      position: pos,
      data: {
        label: node.label,
        type: node.type,
        config: node.config,
      },
    };
  });
}

export function templateToEdges(template: LoopTemplate): Edge[] {
  return template.edges.map((edge) => {
    const strokeStyle =
      edge.edgeType === EdgeType.OnSuccess
        ? { stroke: "#10b981" as const }
        : edge.edgeType === EdgeType.OnFailure
          ? { stroke: "#ef4444" as const, strokeDasharray: "8 4" as const }
          : { stroke: "#f59e0b" as const, strokeDasharray: "4 4" as const };

    // Custom edges read by their name so overlapping outlets stay readable;
    // default/fallback edges keep their fixed role labels.
    const label =
      edge.edgeType === EdgeType.OnSuccess
        ? "success"
        : edge.edgeType === EdgeType.OnFailure
          ? "failure"
          : edge.name?.trim() || "custom";

    const sourceHandle =
      edge.edgeType === EdgeType.OnSuccess
        ? "success"
        : edge.edgeType === EdgeType.OnFailure
          ? "fail"
          : "respond";

    return {
      id: edge.id,
      source: edge.sourceNodeId,
      target: edge.targetNodeId,
      sourceHandle,
      targetHandle: "target-handle",
      // Rounded orthogonal routing; offset pushes the stub clear of the node so
      // 180° turns don't clip it.
      type: "smoothstep",
      pathOptions: { borderRadius: 20, offset: 20 },
      data: { edgeType: edge.edgeType, name: edge.name ?? null },
      animated: edge.edgeType === EdgeType.OnSuccess,
      style: strokeStyle,
      label,
      labelStyle: { fill: "#a0a0b0", fontSize: "0.7rem" },
      labelBgStyle: { fill: "#1e1e30" },
      labelBgPadding: [4, 2] as [number, number],
      labelBgBorderRadius: 4,
    };
  });
}

export function edgesToLoopNodeEdges(edges: Edge[]): LoopNodeEdge[] {
  return edges.map((edge) => {
    const data = edge.data as { edgeType?: EdgeType; name?: string | null };
    const edgeType = data?.edgeType;
    if (!edgeType) {
      throw new Error(
        `Edge "${edge.id}" is missing edgeType in data. This usually means edge.data was lost during React Flow state management.`,
      );
    }
    return {
      id: edge.id,
      sourceNodeId: edge.source,
      targetNodeId: edge.target,
      edgeType,
      name: edgeType === EdgeType.Custom ? (data?.name ?? null) : null,
      maxTraversals: (edge.data as { maxTraversals?: number | null })?.maxTraversals ?? null,
    };
  });
}

export function nodesToLoopNodes(nodes: Node[]): LoopNode[] {
  return nodes.map((node) => {
    const data = node.data as {
      label: string;
      type: string;
      config?: Record<string, unknown>;
    };
    const config = {
      ...data.config,
      __pos: { x: node.position.x, y: node.position.y },
    };
    return {
      id: node.id,
      type: data.type as NodeType,
      label: data.label,
      config: config as Record<string, unknown>,
      maxTraversals: null,
    };
  });
}
