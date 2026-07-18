import type { ReactNode } from "react";
import type { Node } from "@xyflow/react";
import AdapterConfigFields from "../../../components/AdapterConfigFields";
import PromptEditor from "../../../components/PromptEditor";
import {
  NodeType,
  type AiMatchRule,
  type AiProvider,
  type AiToolDefinition,
  type ConditionCase,
  type ConfigFieldDescriptor,
} from "../../../types";
import { AiSessionControls } from "./AiSessionControls";
import { PR_RESERVED_EDGE_NAMES } from "../../../utils/edgeUtils";
import type { AdapterConfigValue, SessionPlaceholderUsage } from "../types";

interface NodeSettingsModalProps {
  selectedNode: Node;
  labelError: string | null;
  nodeLabel: string;
  cmdCommand: string;
  aiPrompt: string;
  aiProvider: string;
  aiTools: string[];
  aiMatchRules: AiMatchRule[];
  customEdgeNames: string[];
  aiUseSession: boolean;
  aiSessionPlaceholder: string;
  aiForkFromPlaceholder: string;
  startCreateWorktree: boolean;
  startRunInstall: boolean;
  humanInputLabel: string;
  humanPrompt: string;
  promptNodePrompt: string;
  prDescriptionTemplate: string;
  prCommentTemplate: string;
  conditionCases: ConditionCase[];
  conditionDefaultEdge: string;
  conditionOutput: string;
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
  onAiMatchRulesChange: (value: AiMatchRule[]) => void;
  onCustomEdgeNamesChange: (value: string[]) => void;
  onAiUseSessionChange: (value: boolean) => void;
  onAiSessionPlaceholderChange: (value: string) => void;
  onAiForkFromPlaceholderChange: (value: string) => void;
  onStartCreateWorktreeChange: (value: boolean) => void;
  onStartRunInstallChange: (value: boolean) => void;
  onHumanInputLabelChange: (value: string) => void;
  onHumanPromptChange: (value: string) => void;
  onPromptNodePromptChange: (value: string) => void;
  onPrDescriptionTemplateChange: (value: string) => void;
  onPrCommentTemplateChange: (value: string) => void;
  onConditionCasesChange: (value: ConditionCase[]) => void;
  onConditionDefaultEdgeChange: (value: string) => void;
  onConditionOutputChange: (value: string) => void;
  onAdapterConfigChange: (name: string, value: AdapterConfigValue) => void;
}

/** Titled group of related fields inside the node settings body. */
function ConfigSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="config-section">
      <h3 className="config-section-title">{title}</h3>
      {children}
    </section>
  );
}

/** Repeatable list of custom edge names, rendered for Human and PR nodes. */
function CustomEdgesEditor({
  names,
  onChange,
  reserved,
}: {
  names: string[];
  onChange: (value: string[]) => void;
  reserved?: readonly string[];
}) {
  const missingReserved = (reserved ?? []).filter((r) => !names.includes(r));
  return (
    <div className="config-field">
      <label>Custom Edges</label>
      <small className="config-help-text">
        Named outlets shown as buttons when this node waits for a human. Define them here, then
        connect each from the node's top handle.
      </small>
      {reserved && reserved.length > 0 && (
        <div className="pr-reserved-edges">
          {missingReserved.map((name) => (
            <button
              key={name}
              type="button"
              className="match-rule-add"
              aria-label={`Add reserved edge ${name}`}
              onClick={() => onChange([...names, name])}
            >
              + {name}
            </button>
          ))}
        </div>
      )}
      {names.map((name, index) => (
        <div key={index} className="match-rule-row">
          <input
            type="text"
            aria-label={`Custom edge name ${index + 1}`}
            value={name}
            onChange={(event) =>
              onChange(names.map((existing, i) => (i === index ? event.target.value : existing)))
            }
            placeholder="Edge name"
          />
          <button
            type="button"
            className="match-rule-remove"
            aria-label={`Remove custom edge ${index + 1}`}
            onClick={() => onChange(names.filter((_, i) => i !== index))}
          >
            ×
          </button>
        </div>
      ))}
      <button type="button" className="match-rule-add" onClick={() => onChange([...names, ""])}>
        + Add edge
      </button>
    </div>
  );
}

/** A single empty Condition case, used when adding a new switch case. */
const EMPTY_CONDITION_CASE: ConditionCase = {
  variant: "TextMatches",
  subject: "",
  pattern: "",
  tag: "",
  edgeName: "",
};

/**
 * Ordered switch cases for a Condition node. Each case picks a predicate
 * (variant) and the custom edge to route to when it holds; the first matching
 * case wins. Mirrors the AI node's Match Rules editor.
 */
function ConditionCasesEditor({
  cases,
  onChange,
}: {
  cases: ConditionCase[];
  onChange: (value: ConditionCase[]) => void;
}) {
  const update = (index: number, patch: Partial<ConditionCase>) =>
    onChange(cases.map((existing, i) => (i === index ? { ...existing, ...patch } : existing)));

  return (
    <div className="config-field">
      <label>Cases</label>
      <small className="config-help-text">
        Each case is evaluated in order; the first whose predicate holds routes to its named custom
        edge. If none match, the default edge is taken.
      </small>
      {cases.map((c, index) => (
        <div key={index} className="condition-case-row">
          <div className="match-rule-row">
            <select
              aria-label={`Case variant ${index + 1}`}
              value={c.variant}
              onChange={(event) => update(index, { variant: event.target.value })}
            >
              <option value="TextMatches">Text matches</option>
              <option value="PrExists">PR exists</option>
              <option value="HasTag">Has tag</option>
            </select>
            <input
              type="text"
              aria-label={`Case edge name ${index + 1}`}
              value={c.edgeName}
              onChange={(event) => update(index, { edgeName: event.target.value })}
              placeholder="Edge name"
            />
            <button
              type="button"
              className="match-rule-remove"
              aria-label={`Remove case ${index + 1}`}
              onClick={() => onChange(cases.filter((_, i) => i !== index))}
            >
              ×
            </button>
          </div>
          {c.variant === "TextMatches" && (
            <>
              <PromptEditor
                id={`condition-case-subject-${index}`}
                rows={2}
                value={c.subject}
                onChange={(value) => update(index, { subject: value })}
              />
              <input
                type="text"
                aria-label={`Case pattern ${index + 1}`}
                value={c.pattern}
                onChange={(event) => update(index, { pattern: event.target.value })}
                placeholder="Regex (case-insensitive)"
              />
            </>
          )}
          {c.variant === "HasTag" && (
            <input
              type="text"
              aria-label={`Case tag ${index + 1}`}
              value={c.tag}
              onChange={(event) => update(index, { tag: event.target.value })}
              placeholder="Work-item tag"
            />
          )}
        </div>
      ))}
      <button
        type="button"
        className="match-rule-add"
        onClick={() => onChange([...cases, { ...EMPTY_CONDITION_CASE }])}
      >
        + Add case
      </button>
    </div>
  );
}

export function NodeSettingsModal({
  selectedNode,
  labelError,
  nodeLabel,
  cmdCommand,
  aiPrompt,
  aiProvider,
  aiTools,
  aiMatchRules,
  customEdgeNames,
  aiUseSession,
  aiSessionPlaceholder,
  aiForkFromPlaceholder,
  startCreateWorktree,
  startRunInstall,
  humanInputLabel,
  humanPrompt,
  promptNodePrompt,
  prDescriptionTemplate,
  prCommentTemplate,
  conditionCases,
  conditionDefaultEdge,
  conditionOutput,
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
  onAiMatchRulesChange,
  onCustomEdgeNamesChange,
  onAiUseSessionChange,
  onAiSessionPlaceholderChange,
  onAiForkFromPlaceholderChange,
  onStartCreateWorktreeChange,
  onStartRunInstallChange,
  onHumanInputLabelChange,
  onHumanPromptChange,
  onPromptNodePromptChange,
  onPrDescriptionTemplateChange,
  onPrCommentTemplateChange,
  onConditionCasesChange,
  onConditionDefaultEdgeChange,
  onConditionOutputChange,
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
              <ConfigSection title="Prompt">
                <div className="config-field">
                  <PromptEditor
                    id="ai-prompt"
                    rows={4}
                    value={aiPrompt}
                    onChange={onAiPromptChange}
                  />
                </div>
              </ConfigSection>

              <ConfigSection title="Session">
                <AiSessionControls
                  key={selectedNode.id}
                  aiUseSession={aiUseSession}
                  aiSessionPlaceholder={aiSessionPlaceholder}
                  aiForkFromPlaceholder={aiForkFromPlaceholder}
                  sessionPlaceholderUsages={sessionPlaceholderUsages}
                  selectedPlaceholderUsage={selectedPlaceholderUsage}
                  onAiUseSessionChange={onAiUseSessionChange}
                  onAiSessionPlaceholderChange={onAiSessionPlaceholderChange}
                  onAiForkFromPlaceholderChange={onAiForkFromPlaceholderChange}
                />
              </ConfigSection>

              <ConfigSection title="Model & tools">
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
              </ConfigSection>

              <ConfigSection title="Routing">
                <div className="config-field">
                  <label>Match Rules</label>
                  <small className="config-help-text">
                    Each rule's pattern is matched case-insensitively against the AI output. The
                    rule matching latest in the output routes to its named custom edge; no match
                    takes the success edge.
                  </small>
                  {aiMatchRules.map((rule, index) => (
                    <div key={index} className="match-rule-row">
                      <input
                        type="text"
                        aria-label={`Match pattern ${index + 1}`}
                        value={rule.pattern}
                        onChange={(event) =>
                          onAiMatchRulesChange(
                            aiMatchRules.map((existing, i) =>
                              i === index ? { ...existing, pattern: event.target.value } : existing,
                            ),
                          )
                        }
                        placeholder="Match pattern (regex)"
                      />
                      <input
                        type="text"
                        aria-label={`Edge name ${index + 1}`}
                        value={rule.edgeName}
                        onChange={(event) =>
                          onAiMatchRulesChange(
                            aiMatchRules.map((existing, i) =>
                              i === index
                                ? { ...existing, edgeName: event.target.value }
                                : existing,
                            ),
                          )
                        }
                        placeholder="Edge name"
                      />
                      <button
                        type="button"
                        className="match-rule-remove"
                        aria-label={`Remove rule ${index + 1}`}
                        onClick={() =>
                          onAiMatchRulesChange(aiMatchRules.filter((_, i) => i !== index))
                        }
                      >
                        ×
                      </button>
                    </div>
                  ))}
                  <button
                    type="button"
                    className="match-rule-add"
                    onClick={() =>
                      onAiMatchRulesChange([...aiMatchRules, { pattern: "", edgeName: "" }])
                    }
                  >
                    + Add rule
                  </button>
                </div>
              </ConfigSection>
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
              <label className="checkbox-label">
                <input
                  type="checkbox"
                  checked={startRunInstall}
                  onChange={(event) => onStartRunInstallChange(event.target.checked)}
                />
                Run ild.config install
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
              <CustomEdgesEditor names={customEdgeNames} onChange={onCustomEdgeNamesChange} />
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
              <small className="config-help-text">
                The PR heartbeat fires these reserved edges on PR state changes (in priority order):
                on_rejected, on_merge_conflict, on_ci_failed, on_approved, on_ci_passed, on_merged,
                on_abandoned. Only wired edges route; there is no fallback to on_success/on_failure,
                so wire <strong>on_merged</strong> and <strong>on_abandoned</strong> to a Cleanup
                path or the run parks forever once the PR closes.
              </small>
              <CustomEdgesEditor
                names={customEdgeNames}
                onChange={onCustomEdgeNamesChange}
                reserved={PR_RESERVED_EDGE_NAMES}
              />
            </>
          )}

          {selectedNodeType === NodeType.Condition && (
            <>
              <small className="config-help-text">
                A switch: each case routes to a named custom edge when its predicate holds, and the
                default edge is taken when none match. No AI, command, or worktree access. Wire each
                edge from the node's top handle.
              </small>

              <ConditionCasesEditor cases={conditionCases} onChange={onConditionCasesChange} />

              <div className="config-field">
                <label htmlFor="condition-default-edge">Default edge</label>
                <input
                  id="condition-default-edge"
                  type="text"
                  value={conditionDefaultEdge}
                  onChange={(event) => onConditionDefaultEdgeChange(event.target.value)}
                  placeholder="Edge name"
                />
                <small className="config-help-text">
                  Taken when no case matches. Connect it from the node's top handle.
                </small>
              </div>

              <div className="config-field">
                <label htmlFor="condition-output">Output</label>
                <PromptEditor
                  id="condition-output"
                  rows={3}
                  value={conditionOutput}
                  onChange={onConditionOutputChange}
                />
                <small className="config-help-text">
                  Emitted on every branch. Defaults to a pass-through of the node input.
                </small>
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
