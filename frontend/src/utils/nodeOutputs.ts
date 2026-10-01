import type { Edge, Node } from "@xyflow/react";
import {
  EdgeType,
  NodeType,
  type AiMatchRule,
  type ConditionCase,
  type NodeOutput,
} from "../types";
import { nodeHasNamedOutputs } from "./edgeUtils";

/**
 * The outputs each node type always holds — success/failure, and a PR node's
 * reserved outputs — keyed by node type, as the server defines them.
 */
export type FixedOutputs = ReadonlyMap<string, NodeOutput[]>;

/**
 * One named output in the node settings Outputs list: the output as edited,
 * and the name it was loaded under (null for one added in this edit), which is
 * how a rename is told apart from a remove plus an add.
 */
export interface OutputRow {
  output: NodeOutput;
  originalName: string | null;
}

/** A Custom edge wired from the node being edited: the output it leaves and where it goes. */
export interface WiredOutput {
  name: string;
  targetLabel: string;
}

/** Success and failure are routed by edge type, never listed or referred to by name. */
function isSuccessOrFailure(name: string): boolean {
  return name === EdgeType.OnSuccess || name === EdgeType.OnFailure;
}

function nameOf(entry: unknown): string | null {
  if (!entry || typeof entry !== "object" || Array.isArray(entry)) return null;
  const name = (entry as { name?: unknown }).name;
  return typeof name === "string" && name.trim() !== "" ? name : null;
}

/** The well-formed entries of a node config's `outputs`. */
function readOutputs(config: Record<string, unknown> | undefined): NodeOutput[] {
  const outputs = config?.outputs;
  if (!Array.isArray(outputs)) return [];
  return outputs.filter((entry): entry is NodeOutput => nameOf(entry) !== null);
}

/** The fixed outputs of `type` that carry a name of their own (a PR node's reserved ones). */
export function fixedNamedOutputs(type: NodeType, fixed: FixedOutputs): NodeOutput[] {
  return (fixed.get(type) ?? []).filter((output) => !isSuccessOrFailure(output.name));
}

/**
 * Whether an output is reserved: the fixed outputs of the node's type flag its
 * name, or a PR node's own entry is flagged.
 */
export function isReservedOutput(type: NodeType, output: NodeOutput, fixed: NodeOutput[]): boolean {
  return (
    (type === NodeType.PR && output.reserved === true) ||
    fixed.some((entry) => entry.reserved === true && entry.name === output.name)
  );
}

/**
 * Whether an output is offered to the person answering in the run: its
 * `visible` when that is a boolean, otherwise the default — hidden for a
 * reserved output, visible for every other.
 */
export function isOutputVisible(output: NodeOutput, reserved: boolean): boolean {
  return typeof output.visible === "boolean" ? output.visible : !reserved;
}

/** The output with `visible` stored only when the choice differs from the default. */
export function withVisibility(
  output: NodeOutput,
  visible: boolean,
  reserved: boolean,
): NodeOutput {
  const next = { ...output };
  if (visible === !reserved) delete next.visible;
  else next.visible = visible;
  return next;
}

/**
 * Looks up, by exact name, whether an output of a node is visible
 * ({@link isOutputVisible}). The first declared entry of that name decides; a
 * name the config does not declare gets the default.
 */
export function outputVisibilityOf(
  type: NodeType,
  config: Record<string, unknown> | undefined,
  fixed: FixedOutputs,
): (name: string) => boolean {
  const declared = readOutputs(config);
  const fixedForType = fixed.get(type) ?? [];
  return (name) => {
    const output = declared.find((entry) => entry.name === name) ?? { name };
    return isOutputVisible(output, isReservedOutput(type, output, fixedForType));
  };
}

/** The success and failure outputs a node has: those its type holds or its config declares. */
export function successFailureOutputs(
  type: NodeType,
  config: Record<string, unknown> | undefined,
  fixed: FixedOutputs,
): string[] {
  const names = new Set(
    [...(fixed.get(type) ?? []), ...readOutputs(config)].map((output) => output.name),
  );
  return [EdgeType.OnSuccess, EdgeType.OnFailure].filter((name) => names.has(name));
}

/** Whether the run asks the person answering to confirm before taking the output. */
export function needsConfirmation(output: NodeOutput): boolean {
  return output.confirm === true;
}

/** The output with `confirm` stored only when it is on. */
export function withConfirmation(output: NodeOutput, confirm: boolean): NodeOutput {
  const next = { ...output };
  if (confirm) next.confirm = true;
  else delete next.confirm;
  return next;
}

/**
 * Looks up, by exact name, whether an output of a node asks for confirmation
 * ({@link needsConfirmation}). The first declared entry of that name decides; a
 * name the config does not declare does not ask.
 */
export function outputConfirmationOf(
  config: Record<string, unknown> | undefined,
): (name: string) => boolean {
  const declared = readOutputs(config);
  return (name) => {
    const output = declared.find((entry) => entry.name === name);
    return output !== undefined && needsConfirmation(output);
  };
}

/**
 * The colour of the output's button in the run, lowercased; null for the
 * default, which is also what a stored value that is not "#rrggbb" gets.
 */
export function buttonColorOf(output: NodeOutput): string | null {
  const color: unknown = output.color;
  return typeof color === "string" && /^#[0-9a-fA-F]{6}$/.test(color) ? color.toLowerCase() : null;
}

/** The output with `color` stored in lowercase, or left out for the default (null). */
export function withColor(output: NodeOutput, color: string | null): NodeOutput {
  const next = { ...output };
  if (color === null) delete next.color;
  else next.color = color.toLowerCase();
  return next;
}

/**
 * Looks up, by exact name, the colour of an output's button
 * ({@link buttonColorOf}). The first declared entry of that name decides; a
 * name the config does not declare has the default.
 */
export function outputColorOf(
  config: Record<string, unknown> | undefined,
): (name: string) => string | null {
  const declared = readOutputs(config);
  return (name) => {
    const output = declared.find((entry) => entry.name === name);
    return output === undefined ? null : buttonColorOf(output);
  };
}

/** The relative luminance of a "#rrggbb" colour, as WCAG 2 defines it. */
function luminanceOf(color: string): number {
  const [r, g, b] = [1, 3, 5].map((start) => {
    const channel = parseInt(color.slice(start, start + 2), 16) / 255;
    return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** Black or white, whichever has the higher WCAG contrast on a "#rrggbb" background. */
export function readableTextOn(color: string): "#000" | "#fff" {
  const luminance = luminanceOf(color);
  return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? "#000" : "#fff";
}

/**
 * A choice made in the node settings for an output that has no row of its
 * own. A `color` of null chooses the default; left out, the colour is untouched.
 */
export interface OutputChoice {
  visible?: boolean;
  confirm?: boolean;
  color?: string | null;
}

function withChoice(output: NodeOutput, choice: OutputChoice, reserved: boolean): NodeOutput {
  const shown =
    choice.visible === undefined ? output : withVisibility(output, choice.visible, reserved);
  const confirmed = choice.confirm === undefined ? shown : withConfirmation(shown, choice.confirm);
  return choice.color === undefined ? confirmed : withColor(confirmed, choice.color);
}

/**
 * `outputs` with each choice written onto the first entry of that name
 * ({@link withVisibility}, {@link withConfirmation}, {@link withColor}); every other entry stays
 * as it is, in place. A chosen output with no entry is declared — as its
 * type's fixed output when it is one — only when a choice differs from the
 * default.
 */
export function applyOutputChoices(
  outputs: unknown,
  type: NodeType,
  fixed: NodeOutput[],
  choices: ReadonlyMap<string, OutputChoice>,
): unknown[] {
  const pending = new Map(choices);
  const result = (Array.isArray(outputs) ? outputs : []).map((entry: unknown) => {
    const name = nameOf(entry);
    const choice = name === null ? undefined : pending.get(name);
    if (name === null || choice === undefined) return entry;
    pending.delete(name);
    const output = entry as NodeOutput;
    return withChoice(output, choice, isReservedOutput(type, output, fixed));
  });
  for (const [name, choice] of pending) {
    const output = fixed.find((entry) => entry.name === name) ?? { name };
    const declared = withChoice(output, choice, isReservedOutput(type, output, fixed));
    if ("visible" in declared || "confirm" in declared || "color" in declared) {
      result.push(declared);
    }
  }
  return result;
}

/** The settings rows for a node's declared named outputs. */
export function outputRowsOf(type: NodeType, config: Record<string, unknown>): OutputRow[] {
  if (!nodeHasNamedOutputs(type)) return [];
  return readOutputs(config)
    .filter((output) => !isSuccessOrFailure(output.name))
    .map((output) => ({ output, originalName: output.name }));
}

/**
 * The outputs a new node of `type` starts with. A Condition also declares the
 * names its starter cases and default route to.
 */
export function initialOutputs(
  type: string,
  fixed: FixedOutputs,
  referenced: string[] = [],
): NodeOutput[] {
  return [
    ...(fixed.get(type) ?? []).map((output) => ({ ...output })),
    ...referenced.map((name) => ({ name })),
  ];
}

/**
 * The named outputs a Custom edge can be wired from: the ones the node declares
 * plus the fixed named outputs of its type.
 */
export function namedOutputNames(node: Node | undefined, fixed: FixedOutputs): string[] {
  if (!node) return [];
  const data = node.data as { type?: NodeType; config?: Record<string, unknown> };
  if (!data.type || !nodeHasNamedOutputs(data.type)) return [];
  const names = [
    ...readOutputs(data.config).map((output) => output.name),
    ...fixedNamedOutputs(data.type, fixed).map((output) => output.name),
  ];
  return [...new Set(names.filter((name) => !isSuccessOrFailure(name)))];
}

/** The Custom edges wired from `sourceId`, each with the label of the node it leads to. */
export function wiredOutputsOf(sourceId: string, edges: Edge[], nodes: Node[]): WiredOutput[] {
  return edges.flatMap((edge) => {
    const data = edge.data as { edgeType?: EdgeType; name?: string | null } | undefined;
    if (edge.source !== sourceId || data?.edgeType !== EdgeType.Custom || !data.name) return [];
    const target = nodes.find((node) => node.id === edge.target);
    const label = (target?.data as { label?: string } | undefined)?.label;
    return [{ name: data.name, targetLabel: label || edge.target }];
  });
}

/**
 * Why each row cannot be saved, or null when it can: a blank name, or a name
 * the node already has — on another row, among the fixed outputs of its type,
 * or success/failure. Names are compared trimmed, as they are saved.
 */
export function outputRowProblems(rows: OutputRow[], fixed: NodeOutput[]): (string | null)[] {
  return rows.map((row, index) => {
    const name = row.output.name.trim();
    if (name === "") return "Give the output a name.";
    const taken =
      isSuccessOrFailure(name) ||
      fixed.some(
        (output) =>
          output.name === name && !rows.some((other) => other.originalName === output.name),
      ) ||
      rows.some(
        (other, i) =>
          i !== index &&
          other.output.name.trim() === name &&
          // A row still named what it was loaded as keeps the name; the one
          // renamed onto it is the one to fix.
          (other.originalName === name || row.originalName !== name),
      );
    return taken ? `The node already has an output named '${name}'. Pick another name.` : null;
  });
}

/** Why each settings field of a node cannot be saved; null where it can. */
export interface SettingsProblems {
  outputs: (string | null)[];
  matchRules: (string | null)[];
  cases: (string | null)[];
  defaultEdge: string | null;
}

/**
 * Everything in a node's settings that the server would refuse, checked where
 * the author can fix it: the output rows ({@link outputRowProblems}), and every
 * output name a match rule, case or default routes to. Such a name must be set
 * and must not be success or failure, which are taken by the edge's own type;
 * any other name is either declared or declared on save. A match rule with no
 * pattern and no output is an unfinished row that saving drops.
 */
export function nodeSettingsProblems(
  type: NodeType,
  settings: {
    rows: OutputRow[];
    fixed: NodeOutput[];
    matchRules: AiMatchRule[];
    cases: ConditionCase[];
    defaultEdge: string;
  },
): SettingsProblems {
  const routeProblem = (name: string, missing: string) => {
    const trimmed = name.trim();
    if (trimmed === "") return missing;
    return isSuccessOrFailure(trimmed)
      ? `'${trimmed}' is taken by the success and failure edges; route to a named output.`
      : null;
  };
  return {
    outputs: outputsAreDerived(type) ? [] : outputRowProblems(settings.rows, settings.fixed),
    matchRules:
      type === NodeType.AI
        ? settings.matchRules.map((rule) =>
            rule.pattern.trim() === "" && rule.edgeName.trim() === ""
              ? null
              : routeProblem(rule.edgeName, "Name the output this rule routes to."),
          )
        : [],
    cases:
      type === NodeType.Condition
        ? settings.cases.map((c) =>
            routeProblem(c.edgeName, "Name the output this case routes to."),
          )
        : [],
    defaultEdge:
      type === NodeType.Condition
        ? routeProblem(
            settings.defaultEdge,
            "A default output is required: name the output taken when no case matches.",
          )
        : null,
  };
}

/** Whether any field of the node's settings has a problem, so they cannot be saved. */
export function hasSettingsProblems(problems: SettingsProblems): boolean {
  return (
    [...problems.outputs, ...problems.matchRules, ...problems.cases].some((p) => p !== null) ||
    problems.defaultEdge !== null
  );
}

/**
 * Old name → new name for every loaded output the rows renamed. A blank name is
 * not a rename: that row is a problem to fix ({@link outputRowProblems}), and
 * until it is fixed the output keeps the name it was loaded under.
 */
export function outputRenames(rows: OutputRow[]): Map<string, string> {
  const renames = new Map<string, string>();
  for (const row of rows) {
    const name = row.output.name.trim();
    if (row.originalName !== null && name !== "" && name !== row.originalName) {
      renames.set(row.originalName, name);
    }
  }
  return renames;
}

/**
 * The node's new `outputs`: every existing entry stays where it is — success
 * and failure untouched, a named one as its row now has it (renamed, with every
 * other field kept) or dropped when its row was deleted — then the added rows,
 * then any name in `referenced` it does not declare now — a name typed into a
 * rule, case or default is declared like any new one, even when an output of
 * that name was deleted in this edit. Entries that are not well-formed are kept
 * for the server to report.
 */
export function mergeOutputs(
  previous: unknown,
  rows: OutputRow[],
  referenced: string[],
): unknown[] {
  const entries = Array.isArray(previous) ? previous : [];
  const placed = new Set<OutputRow>();
  const result: unknown[] = [];
  const place = (row: OutputRow) => {
    placed.add(row);
    result.push({ ...row.output, name: row.output.name.trim() });
  };

  for (const entry of entries) {
    const name = nameOf(entry);
    const row =
      name === null ? undefined : rows.find((r) => r.originalName === name && !placed.has(r));
    if (name === null || isSuccessOrFailure(name)) result.push(entry);
    else if (row) place(row);
  }
  for (const row of rows) {
    if (!placed.has(row)) place(row);
  }

  const declared = new Set(result.map(nameOf));
  for (const name of referenced) {
    if (name !== "" && !isSuccessOrFailure(name) && !declared.has(name)) {
      declared.add(name);
      result.push({ name });
    }
  }
  return result;
}

/** The server's fixed-outputs map, keeping only node types whose value is a list of outputs. */
export function readFixedOutputs(value: unknown): FixedOutputs {
  const fixed = new Map<string, NodeOutput[]>();
  if (!value || typeof value !== "object" || Array.isArray(value)) return fixed;
  for (const [type, outputs] of Object.entries(value)) {
    if (Array.isArray(outputs)) fixed.set(type, readOutputs({ outputs }));
  }
  return fixed;
}

/**
 * Whether a node type's named outputs are not edited as a list but derived on
 * save from what routes to them: an AI node's match rules, a Condition node's
 * cases and default.
 */
export function outputsAreDerived(type: NodeType): boolean {
  return type === NodeType.AI || type === NodeType.Condition;
}

/**
 * The output names a node's settings route to, as they are saved: those of an
 * AI node's finished match rules, or of a Condition node's cases and default.
 */
export function routedOutputNames(
  type: NodeType,
  settings: { matchRules: AiMatchRule[]; cases: ConditionCase[]; defaultEdge: string },
): string[] {
  if (type === NodeType.AI)
    return settings.matchRules
      .filter((rule) => rule.pattern.trim() !== "" && rule.edgeName.trim() !== "")
      .map((rule) => rule.edgeName.trim());
  if (type === NodeType.Condition)
    return [...settings.cases.map((c) => c.edgeName.trim()), settings.defaultEdge.trim()];
  return [];
}

/** The loaded rows nothing routes to any more, which saving a derived node drops. */
export function unroutedRows(rows: OutputRow[], routed: string[]): OutputRow[] {
  return rows.filter((row) => !routed.includes(row.output.name));
}

/** Which of success and failure have an edge wired from `sourceId`. */
export function wiredSuccessFailureOf(sourceId: string, edges: Edge[]): string[] {
  return edges.flatMap((edge) => {
    const edgeType = (edge.data as { edgeType?: EdgeType } | undefined)?.edgeType;
    return edge.source === sourceId && edgeType && isSuccessOrFailure(edgeType) ? [edgeType] : [];
  });
}
