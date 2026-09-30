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
    nodes: [
      {
        id: "n-start",
        type: NodeType.Start,
        label: "Initialize",
        config: { createWorktree: true, __pos: { x: 100, y: 80 } },
      },
      {
        id: "n-cleanup",
        type: NodeType.Cleanup,
        label: "Tidy Up",
        config: { __pos: { x: 100, y: 220 } },
      },
    ],
    edges: [
      {
        id: "e-1",
        sourceNodeId: "n-start",
        targetNodeId: "n-cleanup",
        edgeType: EdgeType.OnSuccess,
      },
    ],
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
    isArchived: false,
  };

  describe("serializeForExport", () => {
    test("produces correct export format with $schema", () => {
      const exportData = serializeForExport(sampleTemplate);

      expect(exportData.$schema).toBe("ild-loop-template/v2");
      expect(exportData.name).toBe("Dev Loop");
      expect(exportData.description).toBe("Standard development loop");
      expect(exportData.recoveryPolicy).toBe(RecoveryPolicy.AutoResume);
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

    test("serializes edges correctly", () => {
      const exportData = serializeForExport(sampleTemplate);

      expect(exportData.edges).toHaveLength(1);
      expect(exportData.edges[0]).toEqual({
        id: "e-1",
        sourceNodeId: "n-start",
        targetNodeId: "n-cleanup",
        edgeType: EdgeType.OnSuccess,
        name: null,
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
          $schema: "ild-loop-template/v2",
          name: "",
          description: "",
          recoveryPolicy: "AutoResume",
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
          $schema: "ild-loop-template/v2",
          name: "Test",
          description: "",
          recoveryPolicy: "InvalidPolicy",
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
          $schema: "ild-loop-template/v2",
          name: "Test",
          description: "",
          recoveryPolicy: "AutoResume",
          edges: [],
        }),
      );
      expect(result.ok).toBe(false);
      if (!result.ok) expect(result.error).toContain("nodes");
    });

    test("rejects malformed node", () => {
      const result = parseImportFile(
        JSON.stringify({
          $schema: "ild-loop-template/v2",
          name: "Test",
          description: "",
          recoveryPolicy: "AutoResume",
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
            $schema: "ild-loop-template/v2",
            name: "Test",
            description: "",
            recoveryPolicy: policy,
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
          $schema: "ild-loop-template/v2",
          name: "  Trimmed Name  ",
          description: "",
          recoveryPolicy: "AutoResume",
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

    test("preserves a custom edge's name through export → import", () => {
      const withCustomEdge = {
        ...sampleTemplate,
        edges: [
          {
            id: "e-custom",
            sourceNodeId: "n-human",
            targetNodeId: "n-ai",
            edgeType: EdgeType.Custom,
            name: "Escalate",
          },
        ],
      };

      const exportData = serializeForExport(withCustomEdge);
      expect(exportData.edges[0].name).toBe("Escalate");

      const loopEdges = exportEdgesToLoopNodeEdges(exportData.edges);
      expect(loopEdges[0].edgeType).toBe(EdgeType.Custom);
      expect(loopEdges[0].name).toBe("Escalate");
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

    test("export → import keeps each output's visible, and puts none on the edges", () => {
      const configs: Record<string, Record<string, unknown>> = {
        "n-start": { outputs: [{ name: "OnSuccess", visible: false }, { name: "OnFailure" }] },
        "n-cmd": { outputs: [{ name: "OnSuccess" }, { name: "OnFailure", visible: false }] },
        "n-human": {
          outputs: [{ name: "OnSuccess" }, { name: "later", visible: false }, { name: "now" }],
        },
        "n-pr": {
          outputs: [
            { name: "on_merged", reserved: true, visible: true },
            { name: "on_ci_failed", reserved: true },
          ],
        },
      };
      const types: Record<string, NodeType> = {
        "n-start": NodeType.Start,
        "n-cmd": NodeType.Cmd,
        "n-human": NodeType.Human,
        "n-pr": NodeType.PR,
      };
      const template: LoopTemplate = {
        ...sampleTemplate,
        nodes: Object.keys(configs).map((id) => ({
          id,
          type: types[id],
          label: id,
          config: configs[id],
        })),
        edges: [
          {
            id: "e-1",
            sourceNodeId: "n-start",
            targetNodeId: "n-cmd",
            edgeType: EdgeType.OnSuccess,
          },
          {
            id: "e-2",
            sourceNodeId: "n-human",
            targetNodeId: "n-pr",
            edgeType: EdgeType.Custom,
            name: "later",
          },
        ],
      };

      const exported = serializeForExport(template);
      const result = parseImportFile(JSON.stringify(exported));

      expect(result.ok).toBe(true);
      if (result.ok) {
        const imported = exportNodesToLoopNodes(result.data.nodes);
        expect(Object.fromEntries(imported.map((n) => [n.id, n.config]))).toEqual(configs);
        expect(exportEdgesToLoopNodeEdges(result.data.edges)).toEqual([
          {
            id: "e-1",
            sourceNodeId: "n-start",
            targetNodeId: "n-cmd",
            edgeType: EdgeType.OnSuccess,
            name: null,
          },
          {
            id: "e-2",
            sourceNodeId: "n-human",
            targetNodeId: "n-pr",
            edgeType: EdgeType.Custom,
            name: "later",
          },
        ]);
      }
    });
  });
});
