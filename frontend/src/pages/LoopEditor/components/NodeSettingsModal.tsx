import { useState, type ReactNode } from "react";
import type { Node } from "@xyflow/react";
import PromptEditor from "../../../components/PromptEditor";
import {
  NodeType,
  type AiMatchRule,
  type AiProvider,
  type AiToolDefinition,
  type ConditionCase,
  type NodeOutput,
} from "../../../types";
import { AiSessionControls } from "./AiSessionControls";
import { resolveProviderForTag } from "../../../utils/providerTags";
import ConfirmModal from "../../../components/ConfirmModal";
import {
  isOutputVisible,
  isReferenced,
  isReservedOutput,
  outputReferences,
  withVisibility,
  hasSettingsProblems,
  nodeSettingsProblems,
  type OutputReferences,
  type OutputRow,
  type WiredOutput,
} from "../../../utils/nodeOutputs";
import type { SessionPlaceholderUsage } from "../types";

interface NodeSettingsModalProps {
  selectedNode: Node;
  labelError: string | null;
  nodeLabel: string;
  cmdCommand: string;
  aiPrompt: string;
  aiProviderTag: string;
  aiTools: string[];
  aiMatchRules: AiMatchRule[];
  outputRows: OutputRow[];
  /** The fixed named outputs of the node's type (a PR node's reserved ones). */
  fixedOutputs: NodeOutput[];
  /** The Custom edges wired from the node, which deleting their output removes. */
  wiredOutputs: WiredOutput[];
  /** The success and failure outputs the node has, by name. */
  successFailureOutputs: string[];
  /** Whether an output that has no row — success, failure, an undeclared fixed one — is visible. */
  isOutputVisible: (name: string) => boolean;
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
  sessionPlaceholderUsages: SessionPlaceholderUsage[];
  selectedPlaceholderUsage?: SessionPlaceholderUsage;
  onClose: () => void;
  onDeleteNode: () => void;
  onSave: () => void;
  onValidateLabel: (value: string) => void;
  onNodeLabelChange: (value: string) => void;
  onCmdCommandChange: (value: string) => void;
  onAiPromptChange: (value: string) => void;
  onAiProviderTagChange: (value: string) => void;
  onAiToolsChange: (value: string[]) => void;
  onAiMatchRulesChange: (value: AiMatchRule[]) => void;
  onOutputRowsChange: (value: OutputRow[]) => void;
  onOutputVisibleChange: (name: string, visible: boolean) => void;
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

/** Whether the output called `name` is offered to the person answering in the run. */
function VisibleToUserToggle({
  name,
  checked,
  onChange,
}: {
  name: string;
  checked: boolean;
  onChange: (visible: boolean) => void;
}) {
  return (
    <label className="checkbox-label output-visible-toggle">
      <input
        type="checkbox"
        aria-label={`Visible to user: ${name}`}
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
      />
      Visible to user
    </label>
  );
}

/** The node's success and failure outputs, which have no name field: they are routed by edge type. */
function SuccessFailureOutputs({
  names,
  isVisible,
  onVisibleChange,
}: {
  names: string[];
  isVisible: (name: string) => boolean;
  onVisibleChange: (name: string, visible: boolean) => void;
}) {
  if (names.length === 0) return null;
  return (
    <div className="config-field">
      <label>Success and failure</label>
      <small className="config-help-text">
        An output that is not visible to the user is not offered to the person answering in the run.
        It still routes as usual.
      </small>
      {names.map((name) => (
        <div key={name} className="match-rule-row">
          <div className="config-read-only output-name">{name}</div>
          <VisibleToUserToggle
            name={name}
            checked={isVisible(name)}
            onChange={(visible) => onVisibleChange(name, visible)}
          />
        </div>
      ))}
    </div>
  );
}

/**
 * A node's named outputs, rendered for Human, AI, PR and Condition nodes. Each
 * row edits one output object, so fields the editor does not show are kept.
 * Reserved outputs — the declared ones and the fixed ones of the node's type
 * the config does not list yet — are shown read-only and cannot be removed.
 * Every output, reserved or not, can be hidden from the person answering. A
 * row with a blank or taken name shows why, and the settings cannot be saved
 * until it is fixed.
 */
function OutputsEditor({
  rows,
  fixed,
  nodeType,
  problems,
  isVisible,
  onChange,
  onVisibleChange,
  onRemove,
}: {
  rows: OutputRow[];
  fixed: NodeOutput[];
  nodeType: NodeType;
  problems: (string | null)[];
  isVisible: (name: string) => boolean;
  onChange: (value: OutputRow[]) => void;
  onVisibleChange: (name: string, visible: boolean) => void;
  onRemove: (row: OutputRow) => void;
}) {
  // By the name it was loaded under: typing a reserved name into a row does not reserve it.
  const isReserved = (row: OutputRow) =>
    isReservedOutput(nodeType, { ...row.output, name: row.originalName ?? "" }, fixed);
  const updateRow = (index: number, update: (output: NodeOutput) => NodeOutput) =>
    onChange(
      rows.map((existing, i) =>
        i === index ? { ...existing, output: update(existing.output) } : existing,
      ),
    );
  const undeclaredFixed = fixed.filter(
    (output) => !rows.some((row) => row.originalName === output.name),
  );
  return (
    <div className="config-field">
      <label>Outputs</label>
      <small className="config-help-text">
        The named outlets this node can take besides success and failure. Declare them here, then
        connect each from the node's top handle.
      </small>
      {rows.map((row, index) => {
        const reserved = isReserved(row);
        const problem = problems[index];
        return (
          <div key={index}>
            <div className="match-rule-row">
              <input
                type="text"
                aria-label={`Output ${index + 1}`}
                aria-invalid={problem !== null}
                className={problem ? "input-error" : ""}
                value={row.output.name}
                readOnly={reserved}
                onChange={(event) =>
                  updateRow(index, (output) => ({ ...output, name: event.target.value }))
                }
                placeholder="Output name"
              />
              <VisibleToUserToggle
                name={row.output.name}
                checked={isOutputVisible(row.output, reserved)}
                onChange={(visible) =>
                  updateRow(index, (output) => withVisibility(output, visible, reserved))
                }
              />
              {!reserved && (
                <button
                  type="button"
                  className="match-rule-remove"
                  aria-label={`Remove output ${index + 1}`}
                  onClick={() => onRemove(row)}
                >
                  ×
                </button>
              )}
            </div>
            {problem && <div className="validation-error">{problem}</div>}
          </div>
        );
      })}
      {undeclaredFixed.map((output) => (
        <div key={output.name} className="match-rule-row">
          <input
            type="text"
            aria-label={`Reserved output ${output.name}`}
            value={output.name}
            readOnly
          />
          <VisibleToUserToggle
            name={output.name}
            checked={isVisible(output.name)}
            onChange={(visible) => onVisibleChange(output.name, visible)}
          />
        </div>
      ))}
      <button
        type="button"
        className="match-rule-add"
        onClick={() => onChange([...rows, { output: { name: "" }, originalName: null }])}
      >
        + Add output
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
 * (variant) and the output to route to when it holds; the first matching
 * case wins. Mirrors the AI node's Match Rules editor.
 */
function ConditionCasesEditor({
  cases,
  problems,
  onChange,
}: {
  cases: ConditionCase[];
  problems: (string | null)[];
  onChange: (value: ConditionCase[]) => void;
}) {
  const update = (index: number, patch: Partial<ConditionCase>) =>
    onChange(cases.map((existing, i) => (i === index ? { ...existing, ...patch } : existing)));

  return (
    <div className="config-field">
      <label>Cases</label>
      <small className="config-help-text">
        Each case is evaluated in order; the first whose predicate holds routes to the output it
        names. If none match, the default output is taken.
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
              aria-label={`Case ${index + 1} output`}
              aria-invalid={problems[index] != null}
              className={problems[index] ? "input-error" : ""}
              value={c.edgeName}
              onChange={(event) => update(index, { edgeName: event.target.value })}
              placeholder="Output name"
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
          {problems[index] && <div className="validation-error">{problems[index]}</div>}
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

/** States the provider the backend will run the node on for `tag`. */
function describeProviderForTag(providers: AiProvider[], tag: string): string {
  const { provider, byTag } = resolveProviderForTag(providers, tag);
  const tagSet = tag.trim() !== "";
  if (byTag && provider) return `Runs on ${provider.name}`;
  if (provider) {
    return tagSet
      ? `No provider has this tag — runs on the default provider (${provider.name})`
      : `Runs on the default provider (${provider.name})`;
  }
  return tagSet
    ? "No provider has this tag and no default provider is configured, so this node cannot run."
    : "No default provider is configured, so this node cannot run.";
}

function AiProviderTagField({
  tag,
  providers,
  onChange,
}: {
  tag: string;
  providers: AiProvider[];
  onChange: (value: string) => void;
}) {
  const suggestions = [...new Set(providers.flatMap((provider) => provider.tags ?? []))].sort(
    (a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }),
  );
  return (
    <div className="config-field">
      <label htmlFor="ai-provider-tag">Provider tag</label>
      <input
        id="ai-provider-tag"
        type="text"
        list="ai-provider-tag-suggestions"
        value={tag}
        onChange={(event) => onChange(event.target.value)}
        placeholder="Default provider"
      />
      <datalist id="ai-provider-tag-suggestions">
        {suggestions.map((suggestion) => (
          <option key={suggestion} value={suggestion} />
        ))}
      </datalist>
      <small className="config-help-text">{describeProviderForTag(providers, tag)}</small>
    </div>
  );
}

export function NodeSettingsModal({
  selectedNode,
  labelError,
  nodeLabel,
  cmdCommand,
  aiPrompt,
  aiProviderTag,
  aiTools,
  aiMatchRules,
  outputRows,
  fixedOutputs,
  wiredOutputs,
  successFailureOutputs,
  isOutputVisible: isUnlistedOutputVisible,
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
  sessionPlaceholderUsages,
  selectedPlaceholderUsage,
  onClose,
  onDeleteNode,
  onSave,
  onValidateLabel,
  onNodeLabelChange,
  onCmdCommandChange,
  onAiPromptChange,
  onAiProviderTagChange,
  onAiToolsChange,
  onAiMatchRulesChange,
  onOutputRowsChange,
  onOutputVisibleChange,
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
}: NodeSettingsModalProps) {
  const selectedNodeType = (selectedNode.data as { type: NodeType }).type;
  const problems = nodeSettingsProblems(selectedNodeType, {
    rows: outputRows,
    fixed: fixedOutputs,
    matchRules: aiMatchRules,
    cases: conditionCases,
    defaultEdge: conditionDefaultEdge,
  });
  const [pendingDelete, setPendingDelete] = useState<OutputRow | null>(null);

  const referencesOf = (row: OutputRow): OutputReferences =>
    outputReferences(row, outputRows, {
      wired: wiredOutputs,
      matchRules: selectedNodeType === NodeType.AI ? aiMatchRules : [],
      cases: selectedNodeType === NodeType.Condition ? conditionCases : [],
      defaultEdge: selectedNodeType === NodeType.Condition ? conditionDefaultEdge : null,
    });

  // Deleting an output takes everything that uses it along: the rules and
  // cases routing to it here, its Custom edges when the settings are saved,
  // and the default edge, which is left blank for the author to choose again.
  const deleteOutput = (row: OutputRow) => {
    const references = referencesOf(row);
    onOutputRowsChange(outputRows.filter((existing) => existing !== row));
    if (references.matchRules.length > 0)
      onAiMatchRulesChange(aiMatchRules.filter((_, i) => !references.matchRules.includes(i)));
    if (references.cases.length > 0)
      onConditionCasesChange(conditionCases.filter((_, i) => !references.cases.includes(i)));
    if (references.defaultEdge) onConditionDefaultEdgeChange("");
  };

  const requestDelete = (row: OutputRow) => {
    if (isReferenced(referencesOf(row))) setPendingDelete(row);
    else deleteOutput(row);
  };

  const pendingReferences = pendingDelete ? referencesOf(pendingDelete) : null;
  const pendingItems = pendingReferences
    ? [
        ...pendingReferences.wired.map((edge) => `The edge to ${edge.targetLabel}`),
        ...pendingReferences.matchRules.map(
          (i) => `Match rule ${i + 1} (${aiMatchRules[i].pattern || "no pattern"})`,
        ),
        ...pendingReferences.cases.map((i) => `Case ${i + 1} (${conditionCases[i].variant})`),
        ...(pendingReferences.defaultEdge ? ["The default output (you will pick a new one)"] : []),
      ]
    : [];

  const outputsEditor = (
    <OutputsEditor
      rows={outputRows}
      fixed={fixedOutputs}
      nodeType={selectedNodeType}
      problems={problems.outputs}
      isVisible={isUnlistedOutputVisible}
      onChange={onOutputRowsChange}
      onVisibleChange={onOutputVisibleChange}
      onRemove={requestDelete}
    />
  );

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
                <AiProviderTagField
                  tag={aiProviderTag}
                  providers={aiProviders}
                  onChange={onAiProviderTagChange}
                />

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
                {outputsEditor}
                <div className="config-field">
                  <label>Match Rules</label>
                  <small className="config-help-text">
                    Each rule's pattern is matched case-insensitively against the AI output. The
                    rule matching latest in the output routes to the output it names; no match takes
                    the success edge. A name not yet in Outputs is added on save.
                  </small>
                  {aiMatchRules.map((rule, index) => (
                    <div key={index}>
                      <div className="match-rule-row">
                        <input
                          type="text"
                          aria-label={`Match pattern ${index + 1}`}
                          value={rule.pattern}
                          onChange={(event) =>
                            onAiMatchRulesChange(
                              aiMatchRules.map((existing, i) =>
                                i === index
                                  ? { ...existing, pattern: event.target.value }
                                  : existing,
                              ),
                            )
                          }
                          placeholder="Match pattern (regex)"
                        />
                        <input
                          type="text"
                          aria-label={`Output name ${index + 1}`}
                          aria-invalid={problems.matchRules[index] != null}
                          className={problems.matchRules[index] ? "input-error" : ""}
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
                          placeholder="Output name"
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
                      {problems.matchRules[index] && (
                        <div className="validation-error">{problems.matchRules[index]}</div>
                      )}
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
              {outputsEditor}
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
                <label htmlFor="pr-comment-template">PR Comment Template (no longer posted)</label>
                <PromptEditor
                  id="pr-comment-template"
                  rows={4}
                  value={prCommentTemplate}
                  onChange={onPrCommentTemplateChange}
                />
                <small className="config-help-text">
                  The node no longer posts this. It used to go out on every re-visit, which meant a
                  round that had already answered on the threads announced itself a second time
                  carrying nothing. The round decides now: an agent calls{" "}
                  <strong>comment_on_pr</strong> when it has something general to say, and a round
                  with nothing to add leaves the pull request quiet. Existing templates keep this
                  field; it simply does nothing.
                </small>
              </div>
              <small className="config-help-text">
                The PR heartbeat fires the reserved outputs listed below on PR state changes; they
                are always present and cannot be renamed or removed. Only wired outputs route; there
                is no fallback to success/failure, so wire <strong>on_merged</strong> and{" "}
                <strong>on_abandoned</strong> to a Cleanup path or the run parks forever once the PR
                closes. <strong>on_comment</strong> carries review and comment items the run has not
                been handed yet — while a review has changes requested, on_rejected outranks it
                every tick, and that round reads the comments with the get_pr_review tool instead.
              </small>
              {outputsEditor}
            </>
          )}

          {selectedNodeType === NodeType.Condition && (
            <>
              <small className="config-help-text">
                A switch: each case routes to one of the node's outputs when its predicate holds,
                and the default output is taken when none match. No AI, command, or worktree access.
                Wire each output from the node's top handle.
              </small>

              {outputsEditor}

              <ConditionCasesEditor
                cases={conditionCases}
                problems={problems.cases}
                onChange={onConditionCasesChange}
              />

              <div className="config-field">
                <label htmlFor="condition-default-edge">Default output</label>
                <input
                  id="condition-default-edge"
                  type="text"
                  className={problems.defaultEdge ? "input-error" : ""}
                  aria-invalid={problems.defaultEdge !== null}
                  value={conditionDefaultEdge}
                  onChange={(event) => onConditionDefaultEdgeChange(event.target.value)}
                  placeholder="Output name"
                />
                {problems.defaultEdge && (
                  <div className="validation-error">{problems.defaultEdge}</div>
                )}
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

          <SuccessFailureOutputs
            names={successFailureOutputs}
            isVisible={isUnlistedOutputVisible}
            onVisibleChange={onOutputVisibleChange}
          />
        </div>
        <div className="node-settings-modal-footer">
          <button className="node-settings-btn-delete" onClick={onDeleteNode}>
            Delete Node
          </button>
          <div className="node-settings-footer-actions">
            <button className="node-settings-btn-cancel" onClick={onClose}>
              Cancel
            </button>
            <button
              className="node-settings-btn-save"
              onClick={onSave}
              aria-disabled={hasSettingsProblems(problems)}
            >
              Save
            </button>
          </div>
        </div>
        <ConfirmModal
          isOpen={pendingDelete !== null}
          title="Delete output"
          message={`Deleting the output '${pendingDelete?.output.name.trim() ?? ""}' also removes:`}
          items={pendingItems}
          confirmText="Delete output"
          onConfirm={() => {
            if (pendingDelete) deleteOutput(pendingDelete);
            setPendingDelete(null);
          }}
          onCancel={() => setPendingDelete(null)}
        />
      </div>
    </div>
  );
}
