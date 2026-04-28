import { WorkItem } from "../types";

interface WorkItemCardProps {
  workItem: WorkItem;
}

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

  return (
    <div className="work-item-card" draggable onDragStart={handleDragStart}>
      <div className="work-item-card-header">
        <span
          className="work-item-priority-dot"
          style={{ backgroundColor: priorityColors[workItem.priority] ?? "#6b7280" }}
        />
      </div>
      <h4 className="work-item-title">{workItem.title}</h4>
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
