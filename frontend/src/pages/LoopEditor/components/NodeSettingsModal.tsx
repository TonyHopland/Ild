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
import { nodeStyleOf } from "../../../utils/nodeStyles";
import ConfirmModal from "../../../components/ConfirmModal";
import {
  buttonColorOf,
  isOutputVisible,
  isReservedOutput,
  needsConfirmation,
  outputsAreDerived,
  routedOutputNames,
  unroutedRows,
  withColor,
  withConfirmation,
  withVisibility,
  hasSettingsProblems,
  nodeSettingsProblems,
  type OutputChoice,
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
  /** Which of success and failure have an edge wired from the node. */
  wiredSuccessFailure: string[];
  /** Whether an output that has no row — success, failure, an undeclared fixed one — is visible. */
  isOutputVisible: (name: string) => boolean;
  /** Whether an output that has no row asks the person answering to confirm before taking it. */
  isOutputConfirmed: (name: string) => boolean;
  /** The button colour of an output that has no row, or null for the default. */
  outputColor: (name: string) => string | null;
  aiUseSession: boolean;
  aiSessionPlaceholder: string;
  aiForkFromPlaceholder: string;
  startCreateWorktree: boolean;
  startRunInstall: boolean;
  humanInputLabel: string;
  humanPrompt: string;
  promptNodePrompt: string;
  prDescriptionTemplate: string;
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
  onOutputChoiceChange: (name: string, choice: OutputChoice) => void;
  onAiUseSessionChange: (value: boolean) => void;
  onAiSessionPlaceholderChange: (value: string) => void;
  onAiForkFromPlaceholderChange: (value: string) => void;
  onStartCreateWorktreeChange: (value: boolean) => void;
  onStartRunInstallChange: (value: boolean) => void;
  onHumanInputLabelChange: (value: string) => void;
  onHumanPromptChange: (value: string) => void;
  onPromptNodePromptChange: (value: string) => void;
  onPrDescriptionTemplateChange: (value: string) => void;
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
  visible,
  unwired = false,
  onChange,
}: {
  name: string;
  visible: boolean;
  /** No edge leaves the output, so it cannot be offered and the choice is not open. */
  unwired?: boolean;
  onChange: (visible: boolean) => void;
}) {
  const state = `${visible ? "Visible to user" : "Hidden from user"}: ${name}`;
  const label = unwired ? `${state} (no edge connected)` : state;
  return (
    <button
      type="button"
      className="output-visible-toggle"
      aria-pressed={visible}
      aria-label={label}
      title={label}
      disabled={unwired}
      onClick={() => onChange(!visible)}
    >
      <svg
        viewBox="0 0 24 24"
        width="16"
        height="16"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M1.5 12S5.5 5 12 5s10.5 7 10.5 7-4 7-10.5 7S1.5 12 1.5 12Z" />
        <circle cx="12" cy="12" r="3" />
        {!visible && <path d="M4 20 20 4" />}
      </svg>
    </button>
  );
}

/**
 * Whether the run asks the person answering to confirm before taking the
 * output called `name`. The choice is closed while the output is not offered.
 */
function ConfirmToggle({
  name,
  confirm,
  closedReason,
  onChange,
}: {
  name: string;
  confirm: boolean;
  /** Why the output is not offered to the person answering, if it is not. */
  closedReason: string | null;
  onChange: (confirm: boolean) => void;
}) {
  const state = `${confirm ? "Asks to confirm" : "Does not ask to confirm"}: ${name}`;
  const label = closedReason ? `${state} (${closedReason})` : state;
  return (
    <button
      type="button"
      className="output-visible-toggle output-confirm-toggle"
      aria-pressed={confirm}
      aria-label={label}
      title={label}
      disabled={closedReason !== null}
      onClick={() => onChange(!confirm)}
    >
      <svg
        viewBox="0 0 24 24"
        width="16"
        height="16"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M12 2.5 4 5.5v6c0 5 3.5 8.5 8 10 4.5-1.5 8-5 8-10v-6Z" />
        {confirm && <path d="m8.5 12 2.5 2.5 4.5-5" />}
      </svg>
    </button>
  );
}

/**
 * The colour of the run's button for the output called `name`; pressing it
 * opens or closes its picker. The choice is closed while the output is not offered.
 */
function ColorSquare({
  name,
  color,
  closedReason,
  open,
  onToggle,
}: {
  name: string;
  /** The "#rrggbb" colour, or null for the default. */
  color: string | null;
  /** Why the output is not offered to the person answering, if it is not. */
  closedReason: string | null;
  open: boolean;
  onToggle: () => void;
}) {
  const state = `Button colour ${color ?? "default"}: ${name}`;
  const label = closedReason ? `${state} (${closedReason})` : state;
  return (
    <button
      type="button"
      className="output-visible-toggle output-color-toggle"
      aria-expanded={open}
      aria-label={label}
      title={label}
      disabled={closedReason !== null}
      onClick={onToggle}
    >
      <span
        className={color ? "output-color-swatch output-color-swatch-set" : "output-color-swatch"}
        style={color ? { backgroundColor: color } : undefined}
        aria-hidden="true"
      />
    </button>
  );
}

/**
 * Picks the colour of the run's button for the output called `name`, or its
 * default. The default shows as black, which the input cannot report as a
 * change, so the colour it shows can also be taken as it is.
 */
function ColorPicker({
  name,
  color,
  onChange,
  onDefault,
}: {
  name: string;
  color: string | null;
  onChange: (color: string) => void;
  onDefault: () => void;
}) {
  const shown = color ?? "#000000";
  return (
    <div className="output-color-picker">
      <input
        type="color"
        aria-label={`Button colour for ${name}`}
        value={shown}
        onChange={(event) => onChange(event.target.value)}
      />
      <button
        type="button"
        className="output-color-action"
        aria-label={`Use this colour for ${name}`}
        onClick={() => onChange(shown)}
      >
        Use this colour
      </button>
      <button type="button" className="output-color-action" onClick={onDefault}>
        Default
      </button>
    </div>
  );
}

const outputRowClass = (visible: boolean) =>
  visible ? "match-rule-row" : "match-rule-row output-row-hidden";

const closedReasonOf = (wired: boolean, visible: boolean) =>
  !wired ? "no edge connected" : visible ? null : "hidden from user";

/**
 * A node's outputs, rendered for Human and PR nodes. Each
 * row edits one output object, so fields the editor does not show are kept.
 * Reserved outputs — the declared ones and the fixed ones of the node's type
 * the config does not list yet — are shown read-only and cannot be removed.
 * Every output, reserved or not, can be hidden from the person answering, and
 * one that is offered can ask them to confirm before it is taken and can have
 * its button coloured; one colour picker is open at a time. A
 * row with a blank or taken name shows why, and the settings cannot be saved
 * until it is fixed.
 */
function OutputsEditor({
  rows,
  fixed,
  successFailure,
  wiredSuccessFailure,
  wiredNames,
  nodeType,
  problems,
  isVisible,
  isConfirmed,
  colorOf,
  onChange,
  onChoiceChange,
  onRemove,
}: {
  rows: OutputRow[];
  fixed: NodeOutput[];
  /** The success and failure outputs to list; they have no name field, being routed by edge type. */
  successFailure: string[];
  wiredSuccessFailure: string[];
  /** The named outputs a Custom edge leaves, by the name the node was opened with. */
  wiredNames: string[];
  nodeType: NodeType;
  problems: (string | null)[];
  isVisible: (name: string) => boolean;
  isConfirmed: (name: string) => boolean;
  colorOf: (name: string) => string | null;
  onChange: (value: OutputRow[]) => void;
  onChoiceChange: (name: string, choice: OutputChoice) => void;
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
  // By the row's key: an output's own name, or "row-<index>" for a declared row.
  const [openColorFor, setOpenColorFor] = useState<string | null>(null);
  const colorControls = (
    key: string,
    name: string,
    color: string | null,
    closedReason: string | null,
    onColor: (color: string | null) => void,
  ) => {
    const open = openColorFor === key && closedReason === null;
    return {
      square: (
        <ColorSquare
          name={name}
          color={color}
          closedReason={closedReason}
          open={open}
          onToggle={() => setOpenColorFor(open ? null : key)}
        />
      ),
      picker: open && (
        <ColorPicker
          name={name}
          color={color}
          onChange={onColor}
          onDefault={() => {
            onColor(null);
            setOpenColorFor(null);
          }}
        />
      ),
    };
  };
  const undeclaredFixed = fixed.filter(
    (output) => !rows.some((row) => row.originalName === output.name),
  );
  return (
    <div className="config-field">
      <label>Outputs</label>
      <small className="config-help-text">
        The outlets this node can take. Add named ones here, then connect each from the node's top
        handle. An output hidden with the eye is not offered to the person answering in the run; it
        still routes as usual. One marked with the shield asks them to confirm before it is taken.
        The square sets the colour of its button.
      </small>
      {successFailure.map((name) => {
        const wired = wiredSuccessFailure.includes(name);
        const visible = wired && isVisible(name);
        const closedReason = closedReasonOf(wired, visible);
        const color = colorControls(name, name, colorOf(name), closedReason, (next) =>
          onChoiceChange(name, { color: next }),
        );
        return (
          <div key={name}>
            <div className={outputRowClass(visible)}>
              <input type="text" aria-label={`Output ${name}`} value={name} readOnly />
              <VisibleToUserToggle
                name={name}
                visible={visible}
                unwired={!wired}
                onChange={(next) => onChoiceChange(name, { visible: next })}
              />
              <ConfirmToggle
                name={name}
                confirm={isConfirmed(name)}
                closedReason={closedReason}
                onChange={(next) => onChoiceChange(name, { confirm: next })}
              />
              {color.square}
              <span className="match-rule-remove-spacer" />
            </div>
            {color.picker}
          </div>
        );
      })}
      {rows.map((row, index) => {
        const reserved = isReserved(row);
        const problem = problems[index];
        const visible = isOutputVisible(row.output, reserved);
        const closedReason = closedReasonOf(
          row.originalName !== null && wiredNames.includes(row.originalName),
          visible,
        );
        const color = colorControls(
          `row-${index}`,
          row.output.name,
          buttonColorOf(row.output),
          closedReason,
          (next) => updateRow(index, (output) => withColor(output, next)),
        );
        return (
          <div key={index}>
            <div className={outputRowClass(visible)}>
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
                visible={visible}
                onChange={(next) =>
                  updateRow(index, (output) => withVisibility(output, next, reserved))
                }
              />
              <ConfirmToggle
                name={row.output.name}
                confirm={needsConfirmation(row.output)}
                closedReason={closedReason}
                onChange={(next) => updateRow(index, (output) => withConfirmation(output, next))}
              />
              {color.square}
              {reserved ? (
                <span className="match-rule-remove-spacer" />
              ) : (
                <button
                  type="button"
                  className="match-rule-remove"
                  aria-label={`Remove output ${index + 1}`}
                  onClick={() => {
                    setOpenColorFor(null);
                    onRemove(row);
                  }}
                >
                  ×
                </button>
              )}
            </div>
            {color.picker}
            {problem && <div className="validation-error">{problem}</div>}
          </div>
        );
      })}
      {undeclaredFixed.map((output) => {
        const visible = isVisible(output.name);
        const closedReason = closedReasonOf(wiredNames.includes(output.name), visible);
        const color = colorControls(
          output.name,
          output.name,
          colorOf(output.name),
          closedReason,
          (next) => onChoiceChange(output.name, { color: next }),
        );
        return (
          <div key={output.name}>
            <div className={outputRowClass(visible)}>
              <input
                type="text"
                aria-label={`Reserved output ${output.name}`}
                value={output.name}
                readOnly
              />
              <VisibleToUserToggle
                name={output.name}
                visible={visible}
                onChange={(next) => onChoiceChange(output.name, { visible: next })}
              />
              <ConfirmToggle
                name={output.name}
                confirm={isConfirmed(output.name)}
                closedReason={closedReason}
                onChange={(next) => onChoiceChange(output.name, { confirm: next })}
              />
              {color.square}
              <span className="match-rule-remove-spacer" />
            </div>
            {color.picker}
          </div>
        );
      })}
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
  wiredSuccessFailure,
  isOutputVisible: isUnlistedOutputVisible,
  isOutputConfirmed: isUnlistedOutputConfirmed,
  outputColor,
  aiUseSession,
  aiSessionPlaceholder,
  aiForkFromPlaceholder,
  startCreateWorktree,
  startRunInstall,
  humanInputLabel,
  humanPrompt,
  promptNodePrompt,
  prDescriptionTemplate,
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
  onOutputChoiceChange,
  onAiUseSessionChange,
  onAiSessionPlaceholderChange,
  onAiForkFromPlaceholderChange,
  onStartCreateWorktreeChange,
  onStartRunInstallChange,
  onHumanInputLabelChange,
  onHumanPromptChange,
  onPromptNodePromptChange,
  onPrDescriptionTemplateChange,
  onConditionCasesChange,
  onConditionDefaultEdgeChange,
  onConditionOutputChange,
}: NodeSettingsModalProps) {
  const selectedNodeType = (selectedNode.data as { type: NodeType }).type;
  const typeStyle = nodeStyleOf(selectedNodeType);
  const problems = nodeSettingsProblems(selectedNodeType, {
    rows: outputRows,
    fixed: fixedOutputs,
    matchRules: aiMatchRules,
    cases: conditionCases,
    defaultEdge: conditionDefaultEdge,
  });
  const parksForAPerson = selectedNodeType === NodeType.Human || selectedNodeType === NodeType.PR;
  const [pendingDelete, setPendingDelete] = useState<OutputRow | null>(null);
  const [confirmingSave, setConfirmingSave] = useState(false);

  // Saving a node whose outputs are derived drops the ones nothing routes to
  // any more, and the edges wired from them.
  const unrouted = outputsAreDerived(selectedNodeType)
    ? unroutedRows(
        outputRows,
        routedOutputNames(selectedNodeType, {
          matchRules: aiMatchRules,
          cases: conditionCases,
          defaultEdge: conditionDefaultEdge,
        }),
      )
    : [];
  const edgesLostOnSave = wiredOutputs.filter((edge) =>
    unrouted.some((row) => row.originalName === edge.name),
  );
  const requestSave = () => {
    if (edgesLostOnSave.length > 0 && !hasSettingsProblems(problems)) setConfirmingSave(true);
    else onSave();
  };

  const wiredFrom = (row: OutputRow) =>
    wiredOutputs.filter((edge) => row.originalName !== null && edge.name === row.originalName);

  const deleteOutput = (row: OutputRow) =>
    onOutputRowsChange(outputRows.filter((existing) => existing !== row));

  const requestDelete = (row: OutputRow) => {
    if (wiredFrom(row).length > 0) setPendingDelete(row);
    else deleteOutput(row);
  };

  const pendingItems = pendingDelete
    ? wiredFrom(pendingDelete).map((edge) => `The edge to ${edge.targetLabel}`)
    : [];

  const outputsEditor = (
    <OutputsEditor
      rows={outputRows}
      fixed={fixedOutputs}
      successFailure={parksForAPerson ? successFailureOutputs : []}
      wiredSuccessFailure={wiredSuccessFailure}
      wiredNames={wiredOutputs.map((edge) => edge.name)}
      nodeType={selectedNodeType}
      problems={problems.outputs}
      isVisible={isUnlistedOutputVisible}
      isConfirmed={isUnlistedOutputConfirmed}
      colorOf={outputColor}
      onChange={onOutputRowsChange}
      onChoiceChange={onOutputChoiceChange}
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
          <h2 className="node-settings-type">
            <span className="node-settings-type-icon" aria-hidden="true">
              {typeStyle.icon}
            </span>
            {selectedNodeType}
          </h2>
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
                <div className="config-field">
                  <label>Match Rules</label>
                  <small className="config-help-text">
                    Each rule's pattern is matched case-insensitively against the AI output. The
                    rule matching latest in the output routes to the output it names; no match takes
                    the success edge. Once saved, connect each output named here from the node's top
                    handle.
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
                A switch: each case routes to the output it names when its predicate holds, and the
                default output is taken when none match. No AI, command, or worktree access. Once
                saved, connect each output named here from the node's top handle.
              </small>

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
              onClick={requestSave}
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
        <ConfirmModal
          isOpen={confirmingSave}
          title="Remove edges"
          message="Nothing routes to these outputs any more. Saving also removes:"
          items={edgesLostOnSave.map((edge) => `The edge '${edge.name}' to ${edge.targetLabel}`)}
          confirmText="Save and remove"
          onConfirm={() => {
            setConfirmingSave(false);
            onSave();
          }}
          onCancel={() => setConfirmingSave(false)}
        />
      </div>
    </div>
  );
}
