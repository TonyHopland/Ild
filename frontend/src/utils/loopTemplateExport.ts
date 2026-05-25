import type {
  LoopTemplate,
  LoopTemplateExport,
  LoopTemplateExportNode,
  LoopTemplateExportEdge,
  LoopNode,
  LoopNodeEdge,
} from "../types";
import { RecoveryPolicy } from "../types";

const EXPORT_SCHEMA = "ild-loop-template/v1" as const;

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
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename.endsWith(".json") ? filename : `${filename}.json`;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
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

  return {
    ok: true,
    data: {
      $schema: EXPORT_SCHEMA,
      name: obj.name.trim(),
      description: obj.description,
      recoveryPolicy: obj.recoveryPolicy as RecoveryPolicy,
      nodes: obj.nodes as LoopTemplateExportNode[],
      edges: obj.edges as LoopTemplateExportEdge[],
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
    maxTraversals: null,
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
    maxTraversals: null,
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
