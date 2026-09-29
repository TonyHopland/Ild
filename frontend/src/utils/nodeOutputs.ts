import type { Node } from "@xyflow/react";
import { EdgeType, NodeType, type NodeOutput } from "../types";
import { nodeHasNamedOutputs } from "./edgeUtils";

/**
 * The outputs each node type always holds — success/failure, and a PR node's
 * reserved outputs — keyed by node type, as the server defines them.
 */
export type FixedOutputs = Partial<Record<string, NodeOutput[]>>;

/**
 * One named output in the node settings Outputs list: the output as edited,
 * and the name it was loaded under (null for one added in this edit), which is
 * how a rename is told apart from a remove plus an add.
 */
export interface OutputRow {
  output: NodeOutput;
  originalName: string | null;
}

/** Success and failure are routed by edge type, never listed or referred to by name. */
export function isSuccessOrFailure(name: string): boolean {
  return name === EdgeType.OnSuccess || name === EdgeType.OnFailure;
}

function nameOf(entry: unknown): string | null {
  if (!entry || typeof entry !== "object" || Array.isArray(entry)) return null;
  const name = (entry as { name?: unknown }).name;
  return typeof name === "string" && name.trim() !== "" ? name : null;
}

/** The well-formed entries of a node config's `outputs`. */
export function readOutputs(config: Record<string, unknown> | undefined): NodeOutput[] {
  const outputs = config?.outputs;
  if (!Array.isArray(outputs)) return [];
  return outputs.filter((entry): entry is NodeOutput => nameOf(entry) !== null);
}

/** The fixed outputs of `type` that carry a name of their own (a PR node's reserved ones). */
export function fixedNamedOutputs(type: NodeType, fixed: FixedOutputs): NodeOutput[] {
  return (fixed[type] ?? []).filter((output) => !isSuccessOrFailure(output.name));
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
    ...(fixed[type] ?? []).map((output) => ({ ...output })),
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

/** Old name → new name for every loaded output the rows renamed (to a non-blank name). */
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
 * other field kept) or dropped when its row was removed — then the added rows,
 * then any name in `referenced` that is still undeclared. Entries that are not
 * well-formed are kept for the server to report.
 */
export function mergeOutputs(
  previous: unknown,
  rows: OutputRow[],
  referenced: string[],
): unknown[] {
  const placed = new Set<OutputRow>();
  const result: unknown[] = [];
  const place = (row: OutputRow) => {
    placed.add(row);
    const name = row.output.name.trim();
    if (name !== "") result.push({ ...row.output, name });
  };

  for (const entry of Array.isArray(previous) ? previous : []) {
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
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  const fixed: FixedOutputs = {};
  for (const [type, outputs] of Object.entries(value)) {
    if (Array.isArray(outputs)) fixed[type] = readOutputs({ outputs });
  }
  return fixed;
}
