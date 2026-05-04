import { LoopRunNode, LoopRunNodeStatus, NodeType } from "../../types";
import EdgeArrow from "./EdgeArrow";
import { EdgeType } from "../../types";

const nodeTypeIcons: Record<string, string> = {
  [NodeType.Start]: "▶",
  [NodeType.Cmd]: "⚙",
  [NodeType.AI]: "🤖",
  [NodeType.Human]: "👤",
  [NodeType.PR]: "🔁",
  [NodeType.Cleanup]: "🧹",
};

const nodeStatusColors: Record<string, string> = {
  [LoopRunNodeStatus.Pending]: "#6b7280",
  [LoopRunNodeStatus.Running]: "#3b82f6",
  [LoopRunNodeStatus.Succeeded]: "#22c55e",
  [LoopRunNodeStatus.Failed]: "#ef4444",
  [LoopRunNodeStatus.Skipped]: "#4b5563",
  [LoopRunNodeStatus.WaitingHuman]: "#f59e0b",
};

interface NodeItemProps {
  runNode: LoopRunNode;
  templateNodeType: NodeType;
  isRunning: boolean;
  edgeType?: EdgeType;
}

function formatDuration(startedAt: string | null, completedAt: string | null): string {
  if (!startedAt) return "";
  const start = new Date(startedAt).getTime();
  const end = completedAt ? new Date(completedAt).getTime() : Date.now();
  const ms = end - start;
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  return `${(ms / 60_000).toFixed(1)}m`;
}

export default function NodeItem({
  runNode,
  templateNodeType,
  isRunning,
  edgeType,
}: NodeItemProps) {
  const statusColor = nodeStatusColors[runNode.status] ?? "#6b7280";
  const icon = nodeTypeIcons[templateNodeType] ?? "?";
  const duration = formatDuration(runNode.startedAt, runNode.completedAt);

  return (
    <div className={`node-item ${isRunning ? "node-running" : ""}`}>
      <div className="node-item-header" style={{ borderLeftColor: statusColor }}>
        <span className="node-item-icon">{icon}</span>
        <span className="node-item-label">{runNode.nodeLabel}</span>
        <span className="node-item-status" style={{ color: statusColor }}>
          {runNode.status}
        </span>
        {duration && <span className="node-item-duration">{duration}</span>}
      </div>

      {edgeType !== undefined && <EdgeArrow edgeType={edgeType} />}
    </div>
  );
}
