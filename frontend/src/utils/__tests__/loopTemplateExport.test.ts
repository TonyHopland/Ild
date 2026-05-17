import { describe, test, expect } from "vite-plus/test";
import {
  serializeForExport,
  parseImportFile,
  exportNodesToLoopNodes,
  exportEdgesToLoopNodeEdges,
} from "../loopTemplateExport";
import { NodeType, EdgeType, RecoveryPolicy } from "../../types";
import type { LoopTemplate } from "../../types";

describe("loopTemplateExport", () => {
  const sampleTemplate: LoopTemplate = {
    id: "tpl-1",
    name: "Dev Loop",
    description: "Standard development loop",
    version: 3,
    recoveryPolicy: RecoveryPolicy.AutoResume,
    maxNodeExecutions: 200,
    maxWallClockHours: 24,
    nodes: [
      {
        id: "n-start",
        type: NodeType.Start,
        label: "Initialize",
        config: { createWorktree: true, __pos: { x: 100, y: 80 } },
        maxTraversals: null,
        retryCount: 0,
        timeoutSeconds: 300,
      },
      {
        id: "n-cleanup",
        type: NodeType.Cleanup,
        label: "Tidy Up",
        config: { __pos: { x: 100, y: 220 } },
        maxTraversals: null,
        retryCount: null,
        timeoutSeconds: null,
      },
    ],
    edges: [
      {
        id: "e-1",
        sourceNodeId: "n-start",
        targetNodeId: "n-cleanup",
        edgeType: EdgeType.OnSuccess,
        maxTraversals: null,
      },
    ],
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
    isArchived: false,
  };

  describe("serializeForExport", () => {
    test("produces correct export format with $schema", () => {
      const exportData = serializeForExport(sampleTemplate);

      expect(exportData.$schema).toBe("ild-loop-template/v1");
      expect(exportData.name).toBe("Dev Loop");
      expect(exportData.description).toBe("Standard development loop");
      expect(exportData.recoveryPolicy).toBe(RecoveryPolicy.AutoResume);
      expect(exportData.maxNodeExecutions).toBe(200);
      expect(exportData.maxWallClockHours).toBe(24);
    });

    test("excludes instance-specific fields (id, createdAt, updatedAt, isArchived)", () => {
      const exportData = serializeForExport(sampleTemplate);

      expect(exportData).not.toHaveProperty("id");
      expect(exportData).not.toHaveProperty("createdAt");
      expect(exportData).not.toHaveProperty("updatedAt");
      expect(exportData).not.toHaveProperty("isArchived");
      expect(exportData).not.toHaveProperty("version");
    });

    test("preserves node positions in config", () => {
      const exportData = serializeForExport(sampleTemplate);

      const startNode = exportData.nodes.find((n) => n.id === "n-start");
      expect(startNode?.config.__pos).toEqual({ x: 100, y: 80 });
    });

    test("includes retryCount and timeoutSeconds on nodes", () => {
      const exportData = serializeForExport(sampleTemplate);

      const startNode = exportData.nodes.find((n) => n.id === "n-start");
      expect(startNode?.retryCount).toBe(0);
      expect(startNode?.timeoutSeconds).toBe(300);

      const cleanupNode = exportData.nodes.find((n) => n.id === "n-cleanup");
      expect(cleanupNode?.retryCount).toBeNull();
      expect(cleanupNode?.timeoutSeconds).toBeNull();
    });

    test("serializes edges correctly", () => {
      const exportData = serializeForExport(sampleTemplate);

      expect(exportData.edges).toHaveLength(1);
      expect(exportData.edges[0]).toEqual({
        id: "e-1",
        sourceNodeId: "n-start",
        targetNodeId: "n-cleanup",
        edgeType: EdgeType.OnSuccess,
      });
    });
  });

  describe("parseImportFile", () => {
    test("parses valid export JSON", () => {
      const exportData = serializeForExport(sampleTemplate);
      const json = JSON.stringify(exportData);
      const result = parseImportFile(json);

      expect(result.ok).toBe(true);
      if (result.ok) {
        expect(result.data.name).toBe("Dev Loop");
        expect(result.data.recoveryPolicy).toBe(RecoveryPolicy.AutoResume);
        expect(result.data.nodes).toHaveLength(2);
        expect(result.data.edges).toHaveLength(1);
      }
    });

    test("rejects invalid JSON", () => {
      const result = parseImportFile("not valid json");
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("Invalid JSON");
    });

    test("rejects missing $schema", () => {
      const result = parseImportFile(
        JSON.stringify({ name: "Test", description: "", nodes: [], edges: [] }),
      );
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("Invalid schema");
    });

    test("rejects wrong $schema version", () => {
      const result = parseImportFile(
        JSON.stringify({
          $schema: "ild-loop-template/v0",
          name: "Test",
          description: "",
          nodes: [],
          edges: [],
        }),
      );
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("Invalid schema");
    });

    test("rejects empty name", () => {
      const result = parseImportFile(
        JSON.stringify({
          $schema: "ild-loop-template/v1",
          name: "",
          description: "",
          recoveryPolicy: "AutoResume",
          maxNodeExecutions: 200,
          maxWallClockHours: 24,
          nodes: [],
          edges: [],
        }),
      );
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("name");
    });

    test("rejects invalid recoveryPolicy", () => {
      const result = parseImportFile(
        JSON.stringify({
          $schema: "ild-loop-template/v1",
          name: "Test",
          description: "",
          recoveryPolicy: "InvalidPolicy",
          maxNodeExecutions: 200,
          maxWallClockHours: 24,
          nodes: [],
          edges: [],
        }),
      );
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("recoveryPolicy");
    });

    test("rejects missing nodes array", () => {
      const result = parseImportFile(
        JSON.stringify({
          $schema: "ild-loop-template/v1",
          name: "Test",
          description: "",
          recoveryPolicy: "AutoResume",
          maxNodeExecutions: 200,
          maxWallClockHours: 24,
          edges: [],
        }),
      );
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("nodes");
    });

    test("rejects malformed node", () => {
      const result = parseImportFile(
        JSON.stringify({
          $schema: "ild-loop-template/v1",
          name: "Test",
          description: "",
          recoveryPolicy: "AutoResume",
          maxNodeExecutions: 200,
          maxWallClockHours: 24,
          nodes: [{ id: "n1" }],
          edges: [],
        }),
      );
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("nodes");
    });

    test("accepts all valid recovery policies", () => {
      for (const policy of [
        RecoveryPolicy.AutoResume,
        RecoveryPolicy.NeedsReview,
        RecoveryPolicy.Cancel,
      ]) {
        const result = parseImportFile(
          JSON.stringify({
            $schema: "ild-loop-template/v1",
            name: "Test",
            description: "",
            recoveryPolicy: policy,
            maxNodeExecutions: 200,
            maxWallClockHours: 24,
            nodes: [
              {
                id: "n1",
                type: NodeType.Start,
                label: "Start",
                config: {},
              },
            ],
            edges: [],
          }),
        );
        expect(result.ok).toBe(true);
      }
    });

    test("trims whitespace from name", () => {
      const result = parseImportFile(
        JSON.stringify({
          $schema: "ild-loop-template/v1",
          name: "  Trimmed Name  ",
          description: "",
          recoveryPolicy: "AutoResume",
          maxNodeExecutions: 200,
          maxWallClockHours: 24,
          nodes: [
            {
              id: "n1",
              type: NodeType.Start,
              label: "Start",
              config: {},
            },
          ],
          edges: [],
        }),
      );
      expect(result.ok).toBe(true);
      if (result.ok) {
        expect(result.data.name).toBe("Trimmed Name");
      }
    });
  });

  describe("exportNodesToLoopNodes", () => {
    test("converts export nodes to LoopNode format", () => {
      const exportData = serializeForExport(sampleTemplate);
      const loopNodes = exportNodesToLoopNodes(exportData.nodes);

      expect(loopNodes).toHaveLength(2);
      expect(loopNodes[0].id).toBe("n-start");
      expect(loopNodes[0].type).toBe(NodeType.Start);
      expect(loopNodes[0].config.__pos).toEqual({ x: 100, y: 80 });
    });

    test("preserves retryCount and timeoutSeconds", () => {
      const exportData = serializeForExport(sampleTemplate);
      const loopNodes = exportNodesToLoopNodes(exportData.nodes);

      const startNode = loopNodes.find((n) => n.id === "n-start");
      expect(startNode?.retryCount).toBe(0);
      expect(startNode?.timeoutSeconds).toBe(300);
    });
  });

  describe("exportEdgesToLoopNodeEdges", () => {
    test("converts export edges to LoopNodeEdge format", () => {
      const exportData = serializeForExport(sampleTemplate);
      const loopEdges = exportEdgesToLoopNodeEdges(exportData.edges);

      expect(loopEdges).toHaveLength(1);
      expect(loopEdges[0].id).toBe("e-1");
      expect(loopEdges[0].sourceNodeId).toBe("n-start");
      expect(loopEdges[0].targetNodeId).toBe("n-cleanup");
      expect(loopEdges[0].edgeType).toBe(EdgeType.OnSuccess);
    });
  });

  describe("round-trip", () => {
    test("export → parseImportFile → exportNodesToLoopNodes preserves positions", () => {
      const exportData = serializeForExport(sampleTemplate);
      const json = JSON.stringify(exportData);
      const result = parseImportFile(json);

      expect(result.ok).toBe(true);
      if (result.ok) {
        const loopNodes = exportNodesToLoopNodes(result.data.nodes);
        const startNode = loopNodes.find((n) => n.id === "n-start");
        expect(startNode?.config.__pos).toEqual({ x: 100, y: 80 });
      }
    });
  });
});
