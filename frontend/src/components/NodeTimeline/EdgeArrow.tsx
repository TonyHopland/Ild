import { EdgeType } from "../../types";

interface EdgeArrowProps {
  edgeType: EdgeType;
  variant?: "retry";
}

export default function EdgeArrow({ edgeType, variant }: EdgeArrowProps) {
  const isRetry = variant === "retry";
  const isSuccess = edgeType === EdgeType.OnSuccess;
  const isRespond = edgeType === EdgeType.OnRespond;
  const color = isRetry ? "#f59e0b" : isSuccess ? "#22c55e" : isRespond ? "#f59e0b" : "#ef4444";
  const label = isRetry ? "retry" : isSuccess ? "success" : isRespond ? "respond" : "failure";

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
