import type { Edge } from "@xyflow/react";
import { EdgeType, NodeType } from "../types";

// Every loop edge renders through the custom LoopEdgeComponent (registered under
// this type) so siblings that share one source/target route can fan apart.
export const LOOP_EDGE_TYPE = "loopEdge";

export interface EdgeConfig {
  source: string;
  target: string;
  edgeType: EdgeType;
  name?: string | null;
  sourceHandle: string;
  targetHandle: string;
}

export interface EdgeConstraintResult {
  allowed: boolean;
  error?: string;
}

// Every node (except the Cleanup sink) has success and failure outputs. Only
// Human, AI, PR and Condition nodes may also declare named outputs, which are
// wired with Custom edges.
const namedOutputNodeTypes = new Set<NodeType>([
  NodeType.Human,
  NodeType.AI,
  NodeType.PR,
  NodeType.Condition,
]);

export function nodeHasNamedOutputs(nodeType: NodeType): boolean {
  return namedOutputNodeTypes.has(nodeType);
}

/**
 * Validates that a node of {@link sourceNodeType} may gain an outgoing edge of
 * {@link edgeType}. Default/fallback edges are single per node; Custom edges are
 * allowed in any number on nodes with named outputs (per-name uniqueness is
 * enforced at confirm time, once the user has picked an output).
 */
export function checkEdgeConstraints(
  sourceId: string,
  sourceNodeType: NodeType,
  edgeType: EdgeType,
  existingEdges: Edge[],
): EdgeConstraintResult {
  if (sourceNodeType === NodeType.Cleanup) {
    return { allowed: false, error: "Cleanup nodes cannot have outgoing edges" };
  }
  if (edgeType === EdgeType.Custom) {
    if (!nodeHasNamedOutputs(sourceNodeType)) {
      return {
        allowed: false,
        error: "Only Human, AI, PR and Condition nodes have named outputs",
      };
    }
    return { allowed: true };
  }

  const exists = existingEdges.some(
    (edge) => edge.source === sourceId && edge.data?.edgeType === edgeType,
  );
  if (exists) {
    return { allowed: false, error: "This edge type is already connected from this node" };
  }
  return { allowed: true };
}

function edgeVisualStyle(edgeType: EdgeType) {
  if (edgeType === EdgeType.OnSuccess) return { stroke: "#10b981" as const };
  if (edgeType === EdgeType.OnFailure)
    return { stroke: "#ef4444" as const, strokeDasharray: "8 4" as const };
  return { stroke: "#f59e0b" as const, strokeDasharray: "4 4" as const };
}

function edgeLabelFor(edgeType: EdgeType, name?: string | null): string {
  if (edgeType === EdgeType.OnSuccess) return "success";
  if (edgeType === EdgeType.OnFailure) return "failure";
  // Custom edges read by their name so overlapping outlets stay distinguishable.
  return name?.trim() || "custom";
}

export function buildEdge(config: EdgeConfig): Edge {
  const name = config.edgeType === EdgeType.Custom ? (config.name ?? null) : null;

  return {
    id: `e-${Date.now()}`,
    source: config.source,
    target: config.target,
    sourceHandle: config.sourceHandle,
    targetHandle: config.targetHandle,
    type: LOOP_EDGE_TYPE,
    animated: config.edgeType === EdgeType.OnSuccess,
    data: { edgeType: config.edgeType, name },
    style: edgeVisualStyle(config.edgeType),
    label: edgeLabelFor(config.edgeType, name),
  };
}

/**
 * Appends {@link edge} to {@link edges}. Unlike React Flow's `addEdge`, this does
 * NOT drop an edge whose source/target/handles match an existing one. Loop nodes
 * legitimately fan several differently-named custom edges between the very same
 * connectors (e.g. a PR node's on_merged / on_rejected / … into one node), and
 * LoopEdgeComponent renders those siblings with staggered labels — but `addEdge`
 * treats same source/target/handles as a duplicate and silently refuses the second
 * one even when its id and name differ. Per-name and per-type uniqueness is still
 * enforced upstream by {@link checkEdgeConstraints} and the connection handlers.
 */
export function appendEdge(edge: Edge, edges: Edge[]): Edge[] {
  return [...edges, edge];
}

// Vertical gap (flow units) between adjacent parallel-edge labels. Siblings that
// share one source/target route all ride the exact same smooth-step path (so each
// still connects cleanly at its handles, just like a lone edge), which would stack
// their labels directly on top of one another; staggering each label by its lane
// keeps every name readable without pulling the lines off their connection points.
export const PARALLEL_LABEL_STAGGER = 22;

/**
 * Where {@link edge} sits among the edges sharing its exact route — same
 * source/target node and the same source/target handle. A lone edge is
 * `{ index: 0, count: 1 }`; siblings get a stable index ordered by edge id so
 * every edge's label staggers to a different slot on every render.
 */
export function parallelEdgeRoute(edges: Edge[], edge: Edge): { index: number; count: number } {
  const siblings = edges
    .filter(
      (candidate) =>
        candidate.source === edge.source &&
        candidate.target === edge.target &&
        (candidate.sourceHandle ?? null) === (edge.sourceHandle ?? null) &&
        (candidate.targetHandle ?? null) === (edge.targetHandle ?? null),
    )
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  return {
    index: Math.max(
      0,
      siblings.findIndex((candidate) => candidate.id === edge.id),
    ),
    count: siblings.length,
  };
}

/**
 * The vertical offset (flow units) a parallel edge's label is staggered from the
 * shared path's midpoint so siblings' labels don't overlap. Labels stagger
 * symmetrically around the midpoint and widen with the sibling count, so two edges
 * sit at ±{@link PARALLEL_LABEL_STAGGER}/2 and a lone edge stays centred.
 */
export function parallelLabelOffset(index: number, count: number): number {
  if (count <= 1) return 0;
  return (index - (count - 1) / 2) * PARALLEL_LABEL_STAGGER;
}

/**
 * Applies a settings edit of {@link sourceId}'s outputs to the Custom edges
 * wired from them: an edge from a renamed output (old name → new name) is
 * renamed, label included, and an edge from a deleted output is removed. Every
 * other edge is left as is.
 */
export function updateOutputEdges(
  edges: Edge[],
  sourceId: string,
  renames: ReadonlyMap<string, string>,
  deleted: ReadonlySet<string>,
): Edge[] {
  if (renames.size === 0 && deleted.size === 0) return edges;
  const outputOf = (edge: Edge) => {
    const data = edge.data as { edgeType?: EdgeType; name?: string | null };
    return edge.source === sourceId && data?.edgeType === EdgeType.Custom && data.name
      ? data.name
      : null;
  };
  return edges.flatMap((edge) => {
    const output = outputOf(edge);
    if (output === null) return [edge];
    if (deleted.has(output)) return [];
    const renamed = renames.get(output);
    return renamed === undefined
      ? [edge]
      : [
          {
            ...edge,
            data: { ...edge.data, name: renamed },
            label: edgeLabelFor(EdgeType.Custom, renamed),
          },
        ];
  });
}
