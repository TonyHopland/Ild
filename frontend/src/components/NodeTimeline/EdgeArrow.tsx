import { EdgeType } from "../../types";
import "./NodeTimeline.css";

interface EdgeArrowProps {
  edgeType: EdgeType;
  /** The traversed edge's name (e.g. "Respond"); shown for custom edges. */
  edgeName?: string | null;
}

export default function EdgeArrow({ edgeType, edgeName }: EdgeArrowProps) {
  const isSuccess = edgeType === EdgeType.OnSuccess;
  const isCustom = edgeType === EdgeType.Custom;
  const color = isSuccess ? "#22c55e" : isCustom ? "#f59e0b" : "#ef4444";
  // Custom edges surface by their name (the actual edge taken); the others by
  // their role. Fall back to "custom" only when the name is unknown.
  const label = isSuccess ? "success" : isCustom ? (edgeName ?? "custom") : "failure";

  return (
    <div className="edge-arrow">
      <div className="edge-arrow-line" style={{ backgroundColor: color }} />
      <div className="edge-arrow-badge" style={{ borderColor: color, color }}>
        {label}
      </div>
      <div className="edge-arrow-chevron" style={{ color }}>
        ▼
      </div>
    </div>
  );
}
