import { useState, useEffect, useCallback, useMemo } from "react";
import { LoopRunNode, LoopRunNodeStatus } from "../types";

/**
 * Shared hook for multi-node expansion in a timeline.
 *
 * - The running (live) node is auto-expanded and cannot be collapsed.
 * - Non-running nodes can be freely expanded/collapsed independently.
 */
export function useNodeExpansion(runNodes: LoopRunNode[]) {
  // Derive runningNodeId once per runNodes change — memoised so consumers
  // don't re-search the array and useCallback deps stay stable.
  const runningNodeId = useMemo(
    () => runNodes.find((n) => n.status === LoopRunNodeStatus.Running)?.id ?? null,
    [runNodes],
  );

  // Initialise with any already-running node to avoid a flash of collapsed content.
  const [expandedNodeIds, setExpandedNodeIds] = useState<string[]>(() =>
    runningNodeId ? [runningNodeId] : [],
  );

  const handleToggleNode = useCallback(
    (nodeId: string) => {
      setExpandedNodeIds((prev) => {
        if (runningNodeId === nodeId) return prev;
        if (prev.includes(nodeId)) return prev.filter((id) => id !== nodeId);
        return [...prev, nodeId];
      });
    },
    [runningNodeId],
  );

  useEffect(() => {
    if (runningNodeId) {
      setExpandedNodeIds((prev) =>
        prev.includes(runningNodeId) ? prev : [...prev, runningNodeId],
      );
    }
  }, [runningNodeId]);

  return { expandedNodeIds, handleToggleNode, runningNodeId };
}
