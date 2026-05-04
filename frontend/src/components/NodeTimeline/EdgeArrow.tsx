import { EdgeType } from "../../types";

interface EdgeArrowProps {
  edgeType: EdgeType;
}

export default function EdgeArrow({ edgeType }: EdgeArrowProps) {
  const isSuccess = edgeType === EdgeType.OnSuccess;
  const color = isSuccess ? "#22c55e" : "#ef4444";
  const label = isSuccess ? "success" : "failure";

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
