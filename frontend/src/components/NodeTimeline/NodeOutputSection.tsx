import { useState } from "react";

const MAX_LINES = 20;

interface NodeOutputSectionProps {
  output: string | null;
  error: string | null;
}

export default function NodeOutputSection({ output, error }: NodeOutputSectionProps) {
  const [expanded, setExpanded] = useState(false);
  const content = error ?? output ?? "";

  if (!content) {
    return (
      <div className="node-detail-section node-output-section">
        <h4>Output</h4>
        <pre className="node-output-content">No output</pre>
      </div>
    );
  }

  const lines = content.split("\n");
  const isTruncated = lines.length > MAX_LINES;
  const displayLines = expanded ? lines : lines.slice(0, MAX_LINES);

  return (
    <div className="node-detail-section node-output-section">
      <h4>Output</h4>
      <pre className="node-output-content">{displayLines.join("\n")}</pre>
      {isTruncated && (
        <button className="node-output-toggle" onClick={() => setExpanded(!expanded)}>
          {expanded ? "Show less" : `Show more (${lines.length - MAX_LINES} more lines)`}
        </button>
      )}
    </div>
  );
}
