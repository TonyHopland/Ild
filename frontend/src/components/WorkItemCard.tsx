import { WorkItem } from "../types";

interface WorkItemCardProps {
  workItem: WorkItem;
}

const REASON_STYLES: Record<string, { bg: string; color: string; border: string }> = {
  "PR Awaiting Merge": { bg: "#1a2744", color: "#60a5fa", border: "#2563eb" },
  "Node Failed": { bg: "#2d1a1a", color: "#f87171", border: "#dc2626" },
  "Rebase Conflict": { bg: "#2d2a1a", color: "#fbbf24", border: "#d97706" },
  "Human Input Needed": { bg: "#1a2d2a", color: "#34d399", border: "#059669" },
};

export default function WorkItemCard({ workItem }: WorkItemCardProps) {
  const handleDragStart = (e: React.DragEvent) => {
    e.dataTransfer.setData("text/plain", workItem.id);
  };

  const priorityColors: Record<string, string> = {
    Low: "#6b7280",
    Medium: "#f59e0b",
    High: "#ef4444",
    Critical: "#dc2626",
  };

  const reasonStyle = workItem.humanFeedbackReason
    ? (REASON_STYLES[workItem.humanFeedbackReason] ?? {
        bg: "#2d2d44",
        color: "#a0a0b0",
        border: "#6366f1",
      })
    : null;

  return (
    <div className="work-item-card" draggable onDragStart={handleDragStart}>
      <div className="work-item-card-header">
        <span
          className="work-item-priority-dot"
          style={{ backgroundColor: priorityColors[workItem.priority] ?? "#6b7280" }}
        />
      </div>
      <h4 className="work-item-title">{workItem.title}</h4>
      {reasonStyle && workItem.humanFeedbackReason && (
        <div
          className="human-feedback-badge"
          style={{
            backgroundColor: reasonStyle.bg,
            color: reasonStyle.color,
            borderLeft: `3px solid ${reasonStyle.border}`,
          }}
        >
          {workItem.humanFeedbackReason}
        </div>
      )}
      <div className="work-item-tags">
        {workItem.labels.map((label) => (
          <span key={label} className="work-item-tag">
            {label}
          </span>
        ))}
      </div>
      <style>{`
        .work-item-card {
          background-color: #2a2a40;
          border-radius: 0.375rem;
          padding: 0.75rem;
          cursor: grab;
          transition: transform 0.1s ease, box-shadow 0.1s ease;
          border: 1px solid #3a3a5c;
        }

        .work-item-card:hover {
          transform: translateY(-1px);
          box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
        }

        .work-item-card:active {
          cursor: grabbing;
        }

        .work-item-card-header {
          display: flex;
          justify-content: flex-end;
          align-items: center;
          margin-bottom: 0.5rem;
        }

        .work-item-priority-dot {
          width: 0.5rem;
          height: 0.5rem;
          border-radius: 50%;
        }

        .work-item-title {
          font-size: 0.875rem;
          font-weight: 500;
          color: #e0e0e0;
          margin-bottom: 0.25rem;
          line-height: 1.4;
        }

        .human-feedback-badge {
          font-size: 0.7rem;
          font-weight: 600;
          padding: 0.2rem 0.5rem;
          border-radius: 0.25rem;
          margin-bottom: 0.375rem;
          text-transform: uppercase;
          letter-spacing: 0.03em;
        }

        .work-item-tags {
          display: flex;
          flex-wrap: wrap;
          gap: 0.25rem;
        }

        .work-item-tag {
          font-size: 0.675rem;
          padding: 0.1rem 0.4rem;
          background-color: #2d2d44;
          border-radius: 0.25rem;
          color: #a0a0b0;
        }
      `}</style>
    </div>
  );
}
