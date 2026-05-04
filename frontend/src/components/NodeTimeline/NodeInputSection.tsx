import { NodeType } from "../../types";

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
      if (effectiveInput.context) {
        const ctx = effectiveInput.context as Record<string, string>;
        const parts: string[] = [];
        if (ctx.workItemTitle) parts.push(`Title: ${ctx.workItemTitle}`);
        if (ctx.workItemDescription) parts.push(`Description: ${ctx.workItemDescription}`);
        if (parts.length > 0) {
          content += `\n\n--- Context ---\n${parts.join("\n")}`;
        }
      }
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

  return (
    <div className="node-detail-section node-input-section">
      <h4>Input</h4>
      <pre className="node-input-content">{content}</pre>
    </div>
  );
}
