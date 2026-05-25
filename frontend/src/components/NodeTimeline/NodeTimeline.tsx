import { useRef, useEffect, useCallback } from "react";
import { LoopRunNode, LoopRunNodeStatus, NodeType, LoopNode, EdgeType } from "../../types";
import NodeItem from "./NodeItem";
import EdgeArrow from "./EdgeArrow";
import { useNodeExpansion } from "../../hooks/useNodeExpansion";

interface NodeTimelineProps {
  runNodes: LoopRunNode[];
  templateNodes: LoopNode[];
}

const defaultNodeType: NodeType = NodeType.Cmd;

function mapNodeType(templateNodes: LoopNode[], nodeId: string): NodeType {
  const tn = templateNodes.find((n) => n.id === nodeId);
  return tn?.type ?? defaultNodeType;
}

export default function NodeTimeline({ runNodes, templateNodes }: NodeTimelineProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const isAtBottomRef = useRef(true);
  const { expandedNodeIds, handleToggleNode, runningNodeId } = useNodeExpansion(runNodes);

  const handleScroll = useCallback(() => {
    const el = containerRef.current;
    if (!el) return;
    isAtBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 50;
  }, []);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    if (isAtBottomRef.current || runningNodeId) {
      el.scrollTo({ top: el.scrollHeight, behavior: "smooth" });
    }
  }, [runNodes.length, runningNodeId]);

  let newNodesCount = 0;
  const showIndicator = !isAtBottomRef.current && runNodes.length > 0;

  return (
    <div className="node-timeline-wrapper">
      <div ref={containerRef} className="node-timeline" onScroll={handleScroll}>
        {runNodes.length === 0 && <div className="node-timeline-empty">Waiting for nodes...</div>}

        {runNodes.map((runNode, index) => {
          const templateNodeType = mapNodeType(templateNodes, runNode.nodeId);
          const templateNode = templateNodes.find((n) => n.id === runNode.nodeId);
          const isRunning = runNode.status === LoopRunNodeStatus.Running;

          let edgeType: EdgeType | undefined;
          if (index > 0) {
            const prevNode = runNodes[index - 1];
            const prevType = mapNodeType(templateNodes, prevNode.nodeId);
            const prevHuman = prevType === "Human";
            if (prevNode.status === LoopRunNodeStatus.Succeeded) {
              edgeType = prevHuman ? EdgeType.OnRespond : EdgeType.OnSuccess;
            } else {
              edgeType = EdgeType.OnFailure;
            }
          }

          return (
            <div key={runNode.id}>
              {edgeType !== undefined && <EdgeArrow edgeType={edgeType} />}
              <NodeItem
                runNode={runNode}
                templateNodeType={templateNodeType}
                templateNodeLabel={templateNode?.label}
                isRunning={isRunning}
                isExpanded={expandedNodeIds.includes(runNode.id)}
                onToggle={() => handleToggleNode(runNode.id)}
              />
            </div>
          );
        })}
      </div>

      {showIndicator && (
        <button
          className="node-timeline-scroll-indicator"
          onClick={() => {
            containerRef.current?.scrollTo({
              top: containerRef.current.scrollHeight,
              behavior: "smooth",
            });
          }}
        >
          ▼{" "}
          {newNodesCount > 0
            ? `${newNodesCount} new node${newNodesCount > 1 ? "s" : ""}`
            : "New activity"}
        </button>
      )}
    </div>
  );
}
