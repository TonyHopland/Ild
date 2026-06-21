import { useCallback } from "react";
import {
  BaseEdge,
  EdgeLabelRenderer,
  getSmoothStepPath,
  useStore,
  type EdgeProps,
} from "@xyflow/react";
import {
  getParallelEdgePath,
  parallelEdgeOffset,
  parallelEdgeRoute,
  PARALLEL_EDGE_INTERACTION_WIDTH,
} from "../utils/edgeUtils";

// Mirrors the rounded orthogonal routing the editor used before parallel edges
// existed, so a lone edge between two nodes looks exactly as it always has.
const SMOOTHSTEP_BORDER_RADIUS = 20;
const SMOOTHSTEP_OFFSET = 20;

/**
 * Renders a loop edge. A lone edge keeps the original smooth-step path. When two
 * or more edges share the same source/target route (e.g. several PR custom edges
 * into one node) they each bow onto a separate lane, so every label stays
 * readable and every edge keeps its own wide hit-area to click.
 */
export default function LoopEdgeComponent({
  id,
  sourceX,
  sourceY,
  targetX,
  targetY,
  sourcePosition,
  targetPosition,
  markerEnd,
  style,
  label,
}: EdgeProps) {
  // Read this edge's lane (index) and how many edges share its route straight
  // from the store so the fan re-balances as siblings are added or removed.
  const route = useStore(
    useCallback(
      (store) => {
        const self = store.edges.find((edge) => edge.id === id);
        if (!self) return "0|1";
        const { index, count } = parallelEdgeRoute(store.edges, self);
        return `${index}|${count}`;
      },
      [id],
    ),
  );
  const [index, count] = route.split("|").map(Number);

  let edgePath: string;
  let labelX: number;
  let labelY: number;
  if (count <= 1) {
    [edgePath, labelX, labelY] = getSmoothStepPath({
      sourceX,
      sourceY,
      sourcePosition,
      targetX,
      targetY,
      targetPosition,
      borderRadius: SMOOTHSTEP_BORDER_RADIUS,
      offset: SMOOTHSTEP_OFFSET,
    });
  } else {
    const fanned = getParallelEdgePath(
      sourceX,
      sourceY,
      targetX,
      targetY,
      parallelEdgeOffset(index, count),
    );
    edgePath = fanned.path;
    labelX = fanned.labelX;
    labelY = fanned.labelY;
  }

  return (
    <>
      <BaseEdge
        id={id}
        path={edgePath}
        markerEnd={markerEnd}
        style={style}
        interactionWidth={count > 1 ? PARALLEL_EDGE_INTERACTION_WIDTH : undefined}
      />
      {label && (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan"
            style={{
              position: "absolute",
              transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)`,
              // Let clicks fall through to the edge's hit-area beneath the label.
              pointerEvents: "none",
              fontSize: "0.7rem",
              color: "#a0a0b0",
              background: "#1e1e30",
              padding: "2px 4px",
              borderRadius: "4px",
            }}
          >
            {label}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}
