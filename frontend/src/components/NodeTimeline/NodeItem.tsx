import { LoopRunNode, LoopRunNodeStatus, NodeType } from "../../types";

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
  [LoopRunNodeStatus.Responded]: "#f59e0b",
};

interface NodeItemProps {
  runNode: LoopRunNode;
  templateNodeType: NodeType;
  templateNodeLabel?: string;
  isRunning: boolean;
  isExpanded: boolean;
  onToggle: () => void;
  onRetry?: (runNodeId: string) => void;
  retryDisabled?: boolean;
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
  templateNodeLabel,
  isRunning,
  isExpanded,
  onToggle,
  onRetry,
  retryDisabled,
  children,
}: NodeItemProps & { children?: React.ReactNode }) {
  const statusColor = nodeStatusColors[runNode.status] ?? "#6b7280";
  const icon = nodeTypeIcons[templateNodeType] ?? "?";
  const duration = formatDuration(runNode.startedAt, runNode.completedAt);
  const displayLabel = templateNodeLabel ?? runNode.nodeLabel;

  return (
    <div className={`node-item ${isRunning ? "node-running" : ""}`}>
      <div className="node-item-header-row">
        <button
          type="button"
          className="node-item-header"
          style={{ borderLeftColor: statusColor }}
          onClick={onToggle}
          aria-expanded={isExpanded}
        >
          <span className="node-item-icon">{icon}</span>
          <span className="node-item-label">{displayLabel}</span>
          <span className="node-item-type">{templateNodeType}</span>
          <span className="node-item-status" style={{ color: statusColor }}>
            {runNode.status}
          </span>
          {duration && <span className="node-item-duration">{duration}</span>}
          <span className={`node-item-chevron ${isExpanded ? "expanded" : ""}`}>▼</span>
        </button>
        {onRetry && !isRunning && (
          <button
            type="button"
            className="node-item-retry"
            disabled={retryDisabled}
            title="Retry from this node with the same input as last time"
            aria-label="Retry from this node"
            onClick={(e) => {
              e.stopPropagation();
              onRetry(runNode.id);
            }}
          >
            ↻ Retry
          </button>
        )}
      </div>
      {isExpanded && children && <div className="node-item-body">{children}</div>}
    </div>
  );
}
