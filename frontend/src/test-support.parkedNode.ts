// Stages the three reads the work item dialog needs to know the outputs of the
// node a run is parked on. Not a test file itself, so it is never collected as
// a suite.
import { vi } from "vite-plus/test";
import { loopRunService, loopTemplateService } from "./services/auth";
import { LoopRunStatus, NodeType } from "./types";
import type { LoopNode, LoopRun, NodeOutput } from "./types";

const SUCCESS_AND_FAILURE: NodeOutput[] = [{ name: "OnSuccess" }, { name: "OnFailure" }];

export const RESERVED_PR_OUTPUTS = [
  "on_rejected",
  "on_merge_conflict",
  "on_ci_failed",
  "on_comment",
  "on_approved",
  "on_ci_passed",
  "on_merged",
  "on_abandoned",
];

/** GET /api/v1/looptemplates/node-outputs, as the server answers it. */
export const FIXED_NODE_OUTPUTS: Record<string, NodeOutput[]> = {
  Start: SUCCESS_AND_FAILURE,
  Cmd: SUCCESS_AND_FAILURE,
  AI: SUCCESS_AND_FAILURE,
  Human: SUCCESS_AND_FAILURE,
  Prompt: SUCCESS_AND_FAILURE,
  PR: [...SUCCESS_AND_FAILURE, ...RESERVED_PR_OUTPUTS.map((name) => ({ name, reserved: true }))],
  Condition: [{ name: "OnFailure" }],
  Cleanup: [],
};

export interface ParkedNode {
  id?: string;
  type: NodeType;
  config?: Record<string, unknown>;
}

/** The template version the staged run is pinned to — not the work item's own template fields. */
export const PINNED_VERSION = { loopTemplateId: "tmpl-pinned", templateVersion: 3 };

/** The run detail of a run parked on `nodeId`, pinned to {@link PINNED_VERSION}. */
export function parkedRun(nodeId: string | null, overrides: Partial<LoopRun> = {}): LoopRun {
  return {
    id: "run-1",
    workItemId: "wi-1",
    ...PINNED_VERSION,
    status: LoopRunStatus.WaitingHuman,
    currentNodeId: nodeId,
    isPaused: false,
    nodeExecutionCount: 1,
    startedAt: "2025-01-02T00:00:00Z",
    completedAt: null,
    nodes: [],
    ...overrides,
  };
}

export function graphNode(node: ParkedNode): LoopNode {
  return {
    id: node.id ?? "n-parked",
    type: node.type,
    label: "Parked",
    config: node.config ?? {},
  };
}

/**
 * Every run read answers with a run parked on `node`, whose pinned version
 * graph holds that node, and the fixed outputs load. The default node is a
 * Human node that hides nothing.
 */
export function stageParkedNode(node: ParkedNode = { type: NodeType.Human }) {
  const parked = graphNode(node);
  return {
    getRun: vi.spyOn(loopRunService, "getById").mockResolvedValue(parkedRun(parked.id)),
    getVersionGraph: vi
      .spyOn(loopTemplateService, "getVersionGraph")
      .mockResolvedValue({ nodes: [parked], edges: [] }),
    getNodeOutputs: vi
      .spyOn(loopTemplateService, "getNodeOutputs")
      .mockResolvedValue(FIXED_NODE_OUTPUTS),
  };
}
