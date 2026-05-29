import { NodeType } from "../../types";
import MarkdownRenderer from "../MarkdownRenderer";

interface NodeOutputSectionProps {
  output: string | null;
  error: string | null;
  nodeType?: NodeType;
}

export default function NodeOutputSection({ output, error, nodeType }: NodeOutputSectionProps) {
  const isMarkdown = nodeType === NodeType.AI;
  const hasOutput = !!output;
  const hasError = !!error;

  return (
    <div className="node-detail-section node-output-section">
      <h4>Output</h4>
      {!hasOutput && !hasError ? (
        <pre className="node-output-content">No output</pre>
      ) : (
        <>
          {hasOutput &&
            (isMarkdown ? (
              <div className="node-output-content">
                <MarkdownRenderer content={output!} />
              </div>
            ) : (
              <pre className="node-output-content">{output}</pre>
            ))}
          {hasError && <pre className="node-output-content node-output-error">{error}</pre>}
        </>
      )}
    </div>
  );
}
