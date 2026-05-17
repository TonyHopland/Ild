import { NodeType } from "../../types";
import MarkdownRenderer from "../MarkdownRenderer";

interface NodeInputSectionProps {
  nodeType: NodeType;
  effectiveInput?: {
    command?: string;
    prompt?: string;
    resolvedPrompt?: string;
    context?: Record<string, unknown>;
    message?: string;
  };
}

export default function NodeInputSection({ nodeType, effectiveInput }: NodeInputSectionProps) {
  let content: string = "";

  if (effectiveInput) {
    if (effectiveInput.command) {
      content = `$ ${effectiveInput.command}`;
    } else if (effectiveInput.resolvedPrompt) {
      content = effectiveInput.resolvedPrompt;
    } else if (effectiveInput.prompt) {
      content = effectiveInput.prompt;
    } else if (effectiveInput.message) {
      content = effectiveInput.message;
    }
  } else {
    const defaults: Record<string, string> = {
      [NodeType.Start]: "Initialized",
      [NodeType.Cleanup]: "Cleanup",
      [NodeType.Cmd]: "Command not available",
      [NodeType.AI]: "Prompt not available",
      [NodeType.Human]: "Feedback request",
      [NodeType.PR]: "PR creation",
    };
    content = defaults[nodeType] ?? "No input data";
  }

  const isMarkdown = nodeType === NodeType.AI;

  return (
    <div className="node-detail-section node-input-section">
      <h4>Input</h4>
      {isMarkdown ? (
        <div className="node-input-content">
          <MarkdownRenderer content={content} />
        </div>
      ) : (
        <pre className="node-input-content">{content}</pre>
      )}
    </div>
  );
}
