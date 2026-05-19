import type { Node } from "@xyflow/react";
import AdapterConfigFields from "../../../components/AdapterConfigFields";
import PromptEditor from "../../../components/PromptEditor";
import {
  NodeType,
  type AiProvider,
  type AiToolDefinition,
  type ConfigFieldDescriptor,
} from "../../../types";
import { AiSessionControls } from "./AiSessionControls";
import type { AdapterConfigValue, SessionPlaceholderUsage } from "../types";

interface NodeSettingsModalProps {
  selectedNode: Node;
  labelError: string | null;
  nodeLabel: string;
  cmdCommand: string;
  aiPrompt: string;
  aiProvider: string;
  aiTools: string[];
  aiRejectPattern: string;
  aiUseSession: boolean;
  aiSessionPlaceholder: string;
  startCreateWorktree: boolean;
  humanInputLabel: string;
  humanPrompt: string;
  promptNodePrompt: string;
  prDescriptionTemplate: string;
  prCommentTemplate: string;
  aiProviders: AiProvider[];
  availableAiTools: AiToolDefinition[];
  adapterConfigSchema: ConfigFieldDescriptor[];
  adapterConfigValues: Record<string, AdapterConfigValue>;
  sessionPlaceholderUsages: SessionPlaceholderUsage[];
  selectedPlaceholderUsage?: SessionPlaceholderUsage;
  onClose: () => void;
  onDeleteNode: () => void;
  onSave: () => void;
  onValidateLabel: (value: string) => void;
  onNodeLabelChange: (value: string) => void;
  onCmdCommandChange: (value: string) => void;
  onAiPromptChange: (value: string) => void;
  onAiProviderChange: (value: string) => void;
  onAiToolsChange: (value: string[]) => void;
  onAiRejectPatternChange: (value: string) => void;
  onAiUseSessionChange: (value: boolean) => void;
  onAiSessionPlaceholderChange: (value: string) => void;
  onStartCreateWorktreeChange: (value: boolean) => void;
  onHumanInputLabelChange: (value: string) => void;
  onHumanPromptChange: (value: string) => void;
  onPromptNodePromptChange: (value: string) => void;
  onPrDescriptionTemplateChange: (value: string) => void;
  onPrCommentTemplateChange: (value: string) => void;
  onAdapterConfigChange: (name: string, value: AdapterConfigValue) => void;
}

export function NodeSettingsModal({
  selectedNode,
  labelError,
  nodeLabel,
  cmdCommand,
  aiPrompt,
  aiProvider,
  aiTools,
  aiRejectPattern,
  aiUseSession,
  aiSessionPlaceholder,
  startCreateWorktree,
  humanInputLabel,
  humanPrompt,
  promptNodePrompt,
  prDescriptionTemplate,
  prCommentTemplate,
  aiProviders,
  availableAiTools,
  adapterConfigSchema,
  adapterConfigValues,
  sessionPlaceholderUsages,
  selectedPlaceholderUsage,
  onClose,
  onDeleteNode,
  onSave,
  onValidateLabel,
  onNodeLabelChange,
  onCmdCommandChange,
  onAiPromptChange,
  onAiProviderChange,
  onAiToolsChange,
  onAiRejectPatternChange,
  onAiUseSessionChange,
  onAiSessionPlaceholderChange,
  onStartCreateWorktreeChange,
  onHumanInputLabelChange,
  onHumanPromptChange,
  onPromptNodePromptChange,
  onPrDescriptionTemplateChange,
  onPrCommentTemplateChange,
  onAdapterConfigChange,
}: NodeSettingsModalProps) {
  const selectedNodeType = (selectedNode.data as { type: NodeType }).type;

  return (
    <div
      className="node-settings-modal-overlay"
      onMouseDown={onClose}
      role="dialog"
      aria-modal="true"
      aria-label="Node Settings"
    >
      <div className="node-settings-modal" onMouseDown={(event) => event.stopPropagation()}>
        <div className="node-settings-modal-header">
          <h2>Node Settings</h2>
          <button className="node-settings-modal-close" onClick={onClose} aria-label="Close">
            ×
          </button>
        </div>
        <div className="node-settings-modal-body">
          <div className="config-field">
            <label htmlFor="node-label">Label</label>
            <input
              id="node-label"
              type="text"
              value={nodeLabel}
              onChange={(event) => onNodeLabelChange(event.target.value)}
              onBlur={(event) => onValidateLabel(event.target.value)}
              className={labelError ? "input-error" : ""}
            />
            {labelError && <div className="validation-error">{labelError}</div>}
          </div>

          <div className="config-field">
            <label>Type</label>
            <div className="config-read-only">{selectedNodeType}</div>
          </div>

          {selectedNodeType === NodeType.Cmd && (
            <>
              <div className="config-field">
                <label htmlFor="cmd-command">Command</label>
                <input
                  id="cmd-command"
                  type="text"
                  value={cmdCommand}
                  onChange={(event) => onCmdCommandChange(event.target.value)}
                />
              </div>
            </>
          )}

          {selectedNodeType === NodeType.AI && (
            <>
              <div className="config-field">
                <label htmlFor="ai-prompt">Prompt</label>
                <PromptEditor
                  id="ai-prompt"
                  rows={4}
                  value={aiPrompt}
                  onChange={onAiPromptChange}
                />
              </div>

              <div className="config-field">
                <label className="checkbox-label">
                  <input
                    type="checkbox"
                    checked={aiUseSession}
                    onChange={(event) => onAiUseSessionChange(event.target.checked)}
                  />
                  Use Session
                </label>
              </div>

              {aiUseSession && (
                <AiSessionControls
                  aiSessionPlaceholder={aiSessionPlaceholder}
                  sessionPlaceholderUsages={sessionPlaceholderUsages}
                  selectedPlaceholderUsage={selectedPlaceholderUsage}
                  onAiSessionPlaceholderChange={onAiSessionPlaceholderChange}
                />
              )}

              <div className="config-field">
                <label htmlFor="ai-provider">AI Provider</label>
                <select
                  id="ai-provider"
                  value={aiProvider}
                  onChange={(event) => onAiProviderChange(event.target.value)}
                >
                  <option value="">Default</option>
                  {aiProviders.map((provider) => (
                    <option key={provider.id} value={provider.id}>
                      {provider.name}
                    </option>
                  ))}
                </select>
              </div>

              {adapterConfigSchema.length > 0 && (
                <AdapterConfigFields
                  schema={adapterConfigSchema}
                  values={adapterConfigValues}
                  onChange={onAdapterConfigChange}
                />
              )}

              <div className="config-field">
                <label>Tool Allowlist</label>
                <div className="tool-checklist">
                  {availableAiTools.map((tool) => (
                    <label key={tool.key} className="checkbox-label" title={tool.description}>
                      <input
                        type="checkbox"
                        checked={aiTools.includes(tool.key)}
                        onChange={(event) => {
                          const nextTools = event.target.checked
                            ? [...aiTools, tool.key]
                            : aiTools.filter((value) => value !== tool.key);
                          onAiToolsChange(nextTools);
                        }}
                      />
                      {tool.label}
                    </label>
                  ))}
                </div>
              </div>

              <div className="config-field">
                <label htmlFor="ai-reject-pattern">Reject Pattern (regex)</label>
                <input
                  id="ai-reject-pattern"
                  type="text"
                  value={aiRejectPattern}
                  onChange={(event) => onAiRejectPatternChange(event.target.value)}
                  placeholder="e.g. I cannot|I'm unable|REJECT"
                />
                <small className="config-help-text">
                  If the AI output matches this pattern case-insensitively, the node fails and
                  routes to the onFailure edge.
                </small>
              </div>
            </>
          )}

          {selectedNodeType === NodeType.Start && (
            <div className="config-field">
              <label className="checkbox-label">
                <input
                  type="checkbox"
                  checked={startCreateWorktree}
                  onChange={(event) => onStartCreateWorktreeChange(event.target.checked)}
                />
                Create worktree
              </label>
            </div>
          )}

          {selectedNodeType === NodeType.Human && (
            <>
              <div className="config-field">
                <label htmlFor="human-input-label">Input Label</label>
                <input
                  id="human-input-label"
                  type="text"
                  value={humanInputLabel}
                  onChange={(event) => onHumanInputLabelChange(event.target.value)}
                />
              </div>
              <div className="config-field">
                <label htmlFor="human-prompt">Human Prompt</label>
                <PromptEditor
                  id="human-prompt"
                  rows={6}
                  value={humanPrompt}
                  onChange={onHumanPromptChange}
                />
              </div>
            </>
          )}

          {selectedNodeType === NodeType.Prompt && (
            <div className="config-field">
              <label htmlFor="prompt-node-prompt">Prompt</label>
              <PromptEditor
                id="prompt-node-prompt"
                rows={6}
                value={promptNodePrompt}
                onChange={onPromptNodePromptChange}
              />
            </div>
          )}

          {selectedNodeType === NodeType.PR && (
            <>
              <div className="config-field">
                <label htmlFor="pr-description-template">PR Description Template</label>
                <PromptEditor
                  id="pr-description-template"
                  rows={4}
                  value={prDescriptionTemplate}
                  onChange={onPrDescriptionTemplateChange}
                />
              </div>
              <div className="config-field">
                <label htmlFor="pr-comment-template">PR Comment Template</label>
                <PromptEditor
                  id="pr-comment-template"
                  rows={4}
                  value={prCommentTemplate}
                  onChange={onPrCommentTemplateChange}
                />
              </div>
            </>
          )}
        </div>
        <div className="node-settings-modal-footer">
          <button className="node-settings-btn-delete" onClick={onDeleteNode}>
            Delete Node
          </button>
          <div className="node-settings-footer-actions">
            <button className="node-settings-btn-cancel" onClick={onClose}>
              Cancel
            </button>
            <button className="node-settings-btn-save" onClick={onSave}>
              Save
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
