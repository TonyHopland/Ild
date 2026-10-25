import type {
  ExportedGlobalVariable,
  GlobalVariable,
  LoopTemplate,
  LoopTemplateExport,
  LoopTemplateExportNode,
  LoopTemplateExportEdge,
  LoopNode,
  LoopNodeEdge,
} from "../types";
import { RecoveryPolicy } from "../types";
import { placeholderKeys, VARIABLE_NAME } from "./promptPlaceholder";
import { saveBlob } from "./saveBlob";

const EXPORT_SCHEMA = "ild-loop-template/v2" as const;

/** Mirrors `PromptPlaceholderRegistry.GlobalPrefix`. */
const GLOBAL_PREFIX = "global.";

/**
 * The global variables the nodes interpolate, distinct ignoring case, in order
 * of first appearance. Mirrors `PromptPlaceholderRegistry.GlobalVariableNames`
 * over the stored config JSON, which is what the server's usage scan reads.
 */
export function usedGlobalVariableNames(nodes: { config: unknown }[]): string[] {
  const seen = new Set<string>();
  const names: string[] = [];
  for (const node of nodes) {
    for (const key of placeholderKeys(JSON.stringify(node.config))) {
      if (!key.toLowerCase().startsWith(GLOBAL_PREFIX)) continue;
      const name = key.slice(GLOBAL_PREFIX.length);
      if (VARIABLE_NAME.test(name) && !seen.has(name.toLowerCase())) {
        seen.add(name.toLowerCase());
        names.push(name);
      }
    }
  }
  return names;
}

/** The stored name and value of each used variable that exists, in the order used. */
export function pickGlobalVariables(
  used: string[],
  all: GlobalVariable[],
): ExportedGlobalVariable[] {
  const byName = new Map(all.map((g) => [g.name.toLowerCase(), g]));
  return used.flatMap((name) => {
    const found = byName.get(name.toLowerCase());
    return found ? [{ name: found.name, value: found.value }] : [];
  });
}

/**
 * Serialize a LoopTemplate (from the editor's current graph + template metadata)
 * into the export JSON format.
 */
export function serializeForExport(template: LoopTemplate): LoopTemplateExport {
  return {
    $schema: EXPORT_SCHEMA,
    name: template.name,
    description: template.description,
    recoveryPolicy: template.recoveryPolicy,
    nodes: template.nodes.map((n) => ({
      id: n.id,
      type: n.type,
      label: n.label,
      config: n.config,
    })),
    edges: template.edges.map((e) => ({
      id: e.id,
      sourceNodeId: e.sourceNodeId,
      targetNodeId: e.targetNodeId,
      edgeType: e.edgeType,
      name: e.name ?? null,
    })),
  };
}

/**
 * Trigger a browser download of the export JSON.
 */
export function downloadExport(exportData: LoopTemplateExport, filename: string) {
  const blob = new Blob([JSON.stringify(exportData, null, 2)], {
    type: "application/json",
  });
  saveBlob(blob, filename.endsWith(".json") ? filename : `${filename}.json`);
}

/** Result of parsing an import file — discriminated union */
export type ParseImportResult =
  | { ok: true; data: LoopTemplateExport }
  | { ok: false; error: string };

/**
 * Parse and validate an import file's JSON content.
 * Returns a discriminated union: { ok: true, data } on success, { ok: false, error } on failure.
 */
export function parseImportFile(raw: string): ParseImportResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { ok: false, error: "Invalid JSON — could not parse the file." };
  }

  if (!parsed || typeof parsed !== "object") {
    return { ok: false, error: "Invalid format — expected a JSON object." };
  }

  const obj = parsed as Record<string, unknown>;

  if (obj["$schema"] !== EXPORT_SCHEMA) {
    return {
      ok: false,
      error: `Invalid schema — expected "${EXPORT_SCHEMA}", got "${typeof obj["$schema"] === "string" ? obj["$schema"] : "missing"}".`,
    };
  }

  if (typeof obj.name !== "string" || !obj.name.trim()) {
    return { ok: false, error: "Invalid format — 'name' must be a non-empty string." };
  }

  if (typeof obj.description !== "string") {
    return { ok: false, error: "Invalid format — 'description' must be a string." };
  }

  if (typeof obj.recoveryPolicy !== "string" || !isRecoveryPolicy(obj.recoveryPolicy)) {
    return {
      ok: false,
      error: `Invalid format — 'recoveryPolicy' must be one of AutoResume, NeedsReview, Cancel.`,
    };
  }

  if (!Array.isArray(obj.nodes) || obj.nodes.length === 0) {
    return { ok: false, error: "Invalid format — 'nodes' must be a non-empty array." };
  }

  for (const node of obj.nodes) {
    if (!validateExportNode(node)) {
      return { ok: false, error: "Invalid format — one or more nodes are malformed." };
    }
  }

  if (!Array.isArray(obj.edges)) {
    return { ok: false, error: "Invalid format — 'edges' must be an array." };
  }

  for (const edge of obj.edges) {
    if (!validateExportEdge(edge)) {
      return { ok: false, error: "Invalid format — one or more edges are malformed." };
    }
  }

  if (obj.globalVariables !== undefined && !validateExportGlobalVariables(obj.globalVariables)) {
    return {
      ok: false,
      error:
        "Invalid format — 'globalVariables' must be a list of { name, value } with unique valid names and string values.",
    };
  }

  return {
    ok: true,
    data: {
      $schema: EXPORT_SCHEMA,
      name: obj.name.trim(),
      description: obj.description,
      recoveryPolicy: obj.recoveryPolicy as RecoveryPolicy,
      nodes: obj.nodes as LoopTemplateExportNode[],
      edges: obj.edges as LoopTemplateExportEdge[],
      ...(obj.globalVariables !== undefined && {
        globalVariables: obj.globalVariables as ExportedGlobalVariable[],
      }),
    },
  };
}

/**
 * Convert a validated export object into a LoopNode[] suitable for
 * the editor's nodesToLoopNodes / API create/update calls.
 */
export function exportNodesToLoopNodes(nodes: LoopTemplateExportNode[]): LoopNode[] {
  return nodes.map((n) => ({
    id: n.id,
    type: n.type,
    label: n.label,
    config: n.config,
  }));
}

/**
 * Convert a validated export object into a LoopNodeEdge[] suitable for
 * the API create/update calls.
 */
export function exportEdgesToLoopNodeEdges(edges: LoopTemplateExportEdge[]): LoopNodeEdge[] {
  return edges.map((e) => ({
    id: e.id,
    sourceNodeId: e.sourceNodeId,
    targetNodeId: e.targetNodeId,
    edgeType: e.edgeType,
    name: e.name ?? null,
  }));
}

// --- Private helpers ---

function isRecoveryPolicy(value: string): value is RecoveryPolicy {
  return (
    value === RecoveryPolicy.AutoResume ||
    value === RecoveryPolicy.NeedsReview ||
    value === RecoveryPolicy.Cancel
  );
}

function validateExportNode(node: unknown): boolean {
  if (!node || typeof node !== "object") return false;
  const obj = node as Record<string, unknown>;
  return (
    typeof obj.id === "string" &&
    typeof obj.type === "string" &&
    typeof obj.label === "string" &&
    typeof obj.config === "object" &&
    obj.config !== null
  );
}

function validateExportEdge(edge: unknown): boolean {
  if (!edge || typeof edge !== "object") return false;
  const obj = edge as Record<string, unknown>;
  return (
    typeof obj.id === "string" &&
    typeof obj.sourceNodeId === "string" &&
    typeof obj.targetNodeId === "string" &&
    typeof obj.edgeType === "string"
  );
}

function validateExportGlobalVariables(value: unknown): boolean {
  if (!Array.isArray(value)) return false;
  const seen = new Set<string>();
  for (const entry of value) {
    if (!entry || typeof entry !== "object") return false;
    const { name, value: text } = entry as Record<string, unknown>;
    if (typeof name !== "string" || !VARIABLE_NAME.test(name) || typeof text !== "string") {
      return false;
    }
    if (seen.has(name.toLowerCase())) return false;
    seen.add(name.toLowerCase());
  }
  return true;
}
