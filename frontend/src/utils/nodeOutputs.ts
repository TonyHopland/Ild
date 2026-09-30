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

/** Everything on a node that uses one of its outputs, as positions in the lists it came from. */
export interface OutputReferences {
  wired: WiredOutput[];
  matchRules: number[];
  cases: number[];
  defaultEdge: boolean;
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
    outputs: outputRowProblems(settings.rows, settings.fixed),
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
 * Everything that uses `row`: the Custom edges wired from the name it was
 * loaded under, and the match rules, cases and default whose name — once this
 * edit's renames are applied, as they are on save — is the row's name.
 */
export function outputReferences(
  row: OutputRow,
  rows: OutputRow[],
  node: {
    wired: WiredOutput[];
    matchRules: AiMatchRule[];
    cases: ConditionCase[];
    defaultEdge: string | null;
  },
): OutputReferences {
  const renames = outputRenames(rows);
  // A row blanked in this edit still answers to the name it was loaded under.
  const name = row.output.name.trim() || row.originalName;
  const routesHere = (reference: string) =>
    !!name && (renames.get(reference.trim()) ?? reference.trim()) === name;
  const indexesOf = <T>(items: T[], referenceOf: (item: T) => string) =>
    items.flatMap((item, index) => (routesHere(referenceOf(item)) ? [index] : []));
  return {
    wired:
      row.originalName === null ? [] : node.wired.filter((edge) => edge.name === row.originalName),
    matchRules: indexesOf(node.matchRules, (rule) => rule.edgeName),
    cases: indexesOf(node.cases, (c) => c.edgeName),
    defaultEdge: node.defaultEdge !== null && routesHere(node.defaultEdge),
  };
}

/** Whether anything uses the output, so deleting it has to be confirmed. */
export function isReferenced(references: OutputReferences): boolean {
  return (
    references.wired.length > 0 ||
    references.matchRules.length > 0 ||
    references.cases.length > 0 ||
    references.defaultEdge
  );
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
