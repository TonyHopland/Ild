import { NodeType } from "../../types";
import MarkdownRenderer from "../MarkdownRenderer";

interface NodeOutputSectionProps {
  output: string | null;
  error: string | null;
  nodeType?: NodeType;
}

export default function NodeOutputSection({ output, error, nodeType }: NodeOutputSectionProps) {
  const content = error ?? output ?? "";
  const isMarkdown = nodeType === NodeType.AI;

  return (
    <div className="node-detail-section node-output-section">
      <h4>Output</h4>
      {!content ? (
        <pre className="node-output-content">No output</pre>
      ) : isMarkdown ? (
        <div className="node-output-content">
          <MarkdownRenderer content={content} />
        </div>
      ) : (
        <pre className="node-output-content">{content}</pre>
      )}
    </div>
  );
}
