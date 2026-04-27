import { WorkItem } from "../types";

interface WorkItemCardProps {
  workItem: WorkItem;
}

export default function WorkItemCard({ workItem }: WorkItemCardProps) {
  const handleDragStart = (e: React.DragEvent) => {
    e.dataTransfer.setData("text/plain", workItem.id);
  };

  const priorityColors: Record<string, string> = {
    low: "#6b7280",
    medium: "#f59e0b",
    high: "#ef4444",
    critical: "#dc2626",
  };

  const typeIcons: Record<string, string> = {
    feature: "F",
    bug: "B",
    task: "T",
    epic: "E",
  };

  return (
    <div className="work-item-card" draggable onDragStart={handleDragStart}>
      <div className="work-item-card-header">
        <span className="work-item-type-badge" style={{ backgroundColor: "#3a3a5c" }}>
          {typeIcons[workItem.type] ?? "?"}
        </span>
        <span
          className="work-item-priority-dot"
          style={{ backgroundColor: priorityColors[workItem.priority] ?? "#6b7280" }}
        />
      </div>
      <h4 className="work-item-title">{workItem.title}</h4>
      {workItem.assigneeName && <div className="work-item-assignee">{workItem.assigneeName}</div>}
      <div className="work-item-tags">
        {workItem.tags.map((tag) => (
          <span key={tag} className="work-item-tag">
            {tag}
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
          justify-content: space-between;
          align-items: center;
          margin-bottom: 0.5rem;
        }

        .work-item-type-badge {
          display: inline-flex;
          align-items: center;
          justify-content: center;
          width: 1.5rem;
          height: 1.5rem;
          border-radius: 0.25rem;
          font-size: 0.7rem;
          font-weight: 700;
          color: #e0e0e0;
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

        .work-item-assignee {
          font-size: 0.75rem;
          color: #808090;
          margin-bottom: 0.25rem;
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
