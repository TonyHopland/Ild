import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";

vi.mock("../../services/auth", () => ({
  loopRunService: {
    getById: vi.fn(),
    getEvents: vi.fn(),
    getPayload: vi.fn(),
    getSessionPreview: vi.fn(),
    cancel: vi.fn(),
    pause: vi.fn(),
    resume: vi.fn(),
  },
  loopTemplateService: {
    getVersionGraph: vi.fn(),
  },
}));

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: vi.fn().mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  }),
}));

import EventLogViewer from "./index";
import { loopRunService, loopTemplateService } from "../../services/auth";

const mockEvents = [
  {
    sequence: 1,
    runId: "test-run",
    eventType: "NodeStarted",
    nodeId: null,
    payload: "First event message",
    timestamp: "2025-01-01T00:00:00Z",
    hasPayload: false,
    runNodeId: "run-node-1",
  },
  {
    sequence: 2,
    runId: "test-run",
    eventType: "NodeCompleted",
    nodeId: "node-1",
    payload: "Second event message",
    timestamp: "2025-01-01T00:01:00Z",
    hasPayload: false,
    runNodeId: "run-node-1",
  },
];

const mockEventsWithAIInput = [
  {
    sequence: 1,
    runId: "test-run-ai",
    eventType: "NodeStarted",
    nodeId: "node-ai",
    payload:
      'AI Node started\n{"nodeType":"AI","prompt":"Analyze {{WorkItem.Title}}","context":{"workItemTitle":"Test WI"}}',
    timestamp: "2025-01-01T00:00:00Z",
    hasPayload: false,
    runNodeId: "run-node-ai",
  },
  {
    sequence: 2,
    runId: "test-run-ai",
    eventType: "NodeCompleted",
    nodeId: "node-ai",
    payload: 'AI Node succeeded\n{"output":"analysis result","resolvedPrompt":"Analyze Test WI"}',
    timestamp: "2025-01-01T00:01:00Z",
    hasPayload: false,
    runNodeId: "run-node-ai",
  },
];

const mockPage = {
  entries: mockEvents,
  nextCursor: 2,
  hasMore: false,
};

const mockRun = {
  id: "test-run",
  workItemId: "work-1",
  loopTemplateId: "template-1",
  templateVersion: 1,
  status: "Completed",
  currentNodeId: null,
  isPaused: false,
  nodeExecutionCount: 2,
  startedAt: "2025-01-01T00:00:00Z",
  completedAt: "2025-01-01T00:02:00Z",
  availableSessions: [
    {
      adapterName: "OpenCode",
      sessionId: "ses_current",
      createdAt: "2025-01-01T00:00:00Z",
      updatedAt: "2025-01-01T00:01:00Z",
      isCurrent: true,
      placeholders: ["research"],
    },
  ],
  availableVariables: [
    {
      name: "handoff_note",
      value: "# Handoff\n\nShip **it**",
      createdAt: "2025-01-01T00:00:00Z",
      updatedAt: "2025-01-01T00:01:30Z",
    },
  ],
  nodes: [
    {
      id: "run-node-1",
      nodeId: "node-1",
      nodeLabel: "Start Node",
      status: "Succeeded",
      output: "output data",
      error: null,
      startedAt: "2025-01-01T00:00:00Z",
      completedAt: "2025-01-01T00:01:00Z",
      executionCount: 1,
    },
  ],
};

const mockTemplateGraph = {
  nodes: [
    {
      id: "node-1",
      type: "Start",
      label: "Start Node",
      config: {},
      maxTraversals: null,
    },
  ],
  edges: [],
};

const mockRunWithAI = {
  id: "test-run-ai",
  workItemId: "work-1",
  loopTemplateId: "template-1",
  templateVersion: 1,
  status: "Completed",
  currentNodeId: null,
  isPaused: false,
  nodeExecutionCount: 1,
  startedAt: "2025-01-01T00:00:00Z",
  completedAt: "2025-01-01T00:02:00Z",
  availableSessions: [
    {
      adapterName: "OpenCode",
      sessionId: "ses_ai",
      createdAt: "2025-01-01T00:00:00Z",
      updatedAt: "2025-01-01T00:01:00Z",
      isCurrent: true,
      placeholders: ["analysis"],
    },
  ],
  nodes: [
    {
      id: "run-node-ai",
      nodeId: "node-ai",
      nodeLabel: "AI Node",
      status: "Succeeded",
      output: "analysis result",
      error: null,
      startedAt: "2025-01-01T00:00:00Z",
      completedAt: "2025-01-01T00:01:00Z",
      executionCount: 1,
    },
  ],
};

const mockTemplateGraphAI = {
  nodes: [
    {
      id: "node-ai",
      type: "AI",
      label: "AI Node",
      config: {},
      maxTraversals: null,
    },
  ],
  edges: [],
};

const mockRunWithMultipleNodes = {
  id: "test-run-multi",
  workItemId: "work-1",
  loopTemplateId: "template-1",
  templateVersion: 1,
  status: "Running",
  currentNodeId: "node-2",
  isPaused: false,
  nodeExecutionCount: 2,
  startedAt: "2025-01-01T00:00:00Z",
  completedAt: null,
  availableSessions: [],
  nodes: [
    {
      id: "run-node-1",
      nodeId: "node-1",
      nodeLabel: "Start Node",
      status: "Succeeded",
      output: "start output",
      error: null,
      startedAt: "2025-01-01T00:00:00Z",
      completedAt: "2025-01-01T00:01:00Z",
      executionCount: 1,
    },
    {
      id: "run-node-2",
      nodeId: "node-2",
      nodeLabel: "Cmd Node",
      status: "Running",
      output: null,
      error: null,
      startedAt: "2025-01-01T00:01:00Z",
      completedAt: null,
      executionCount: 1,
    },
    {
      id: "run-node-3",
      nodeId: "node-3",
      nodeLabel: "AI Node",
      status: "Pending",
      output: null,
      error: null,
      startedAt: null,
      completedAt: null,
      executionCount: 0,
    },
  ],
};

const mockTemplateGraphMulti = {
  nodes: [
    {
      id: "node-1",
      type: "Start",
      label: "Start Node",
      config: {},
      maxTraversals: null,
    },
    {
      id: "node-2",
      type: "Cmd",
      label: "Cmd Node",
      config: {},
      maxTraversals: null,
    },
    {
      id: "node-3",
      type: "AI",
      label: "AI Node",
      config: {},
      maxTraversals: null,
    },
  ],
  edges: [],
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

beforeEach(() => {
  (loopRunService.getById as ReturnType<typeof vi.fn>).mockResolvedValue(mockRun);
  (loopRunService.getEvents as ReturnType<typeof vi.fn>).mockResolvedValue(mockPage);
  (loopRunService.getPayload as ReturnType<typeof vi.fn>).mockResolvedValue({
    payload: "large payload content",
  });
  (loopRunService.getSessionPreview as ReturnType<typeof vi.fn>).mockResolvedValue({
    adapterName: "OpenCode",
    sessionId: "ses_current",
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:01:00Z",
    sessionJson: '{"id":"ses_current","messages":[{"role":"user","content":"hello"}]}',
  });
  (loopTemplateService.getVersionGraph as ReturnType<typeof vi.fn>).mockResolvedValue(
    mockTemplateGraph,
  );
});

describe("EventLogViewer", () => {
  test("mocks are set up correctly", async () => {
    const result = await loopRunService.getById("test-run");
    expect(result).toEqual(mockRun);
  });

  function renderComponent() {
    return render(
      <MemoryRouter initialEntries={["/loop-runs/test-run/events"]}>
        <Routes>
          <Route path="/loop-runs/:runId/events" element={<EventLogViewer />} />
        </Routes>
      </MemoryRouter>,
    );
  }

  test("calls getById on mount", async () => {
    renderComponent();
    await waitFor(() => {
      expect(loopRunService.getById).toHaveBeenCalledWith("test-run");
    });
  });

  test("renders node timeline with node label and status", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Start Node")).toBeTruthy();
    });
    expect(screen.getByText("Succeeded")).toBeTruthy();
  });

  test("renders node input section", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Start Node")).toBeTruthy();
    });
    fireEvent.click(screen.getByText("Start Node"));
    await waitFor(() => {
      expect(screen.getByText("Input")).toBeTruthy();
    });
  });

  test("renders node events section with event count", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Start Node")).toBeTruthy();
    });
    fireEvent.click(screen.getByText("Start Node"));
    await waitFor(() => {
      expect(screen.getByText("Events (2)")).toBeTruthy();
    });
  });

  test("renders node output section", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Start Node")).toBeTruthy();
    });
    fireEvent.click(screen.getByText("Start Node"));
    await waitFor(() => {
      expect(screen.getByText("Output")).toBeTruthy();
    });
    expect(screen.getByText("output data")).toBeTruthy();
  });

  test("renders run details header with run id and status", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Completed")).toBeTruthy();
    });
  });

  test("renders available AI sessions for the run", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Available AI Sessions")).toBeTruthy();
    });
    expect(screen.getByText("ses_current")).toBeTruthy();
    expect(screen.getByText(/Placeholders: research/)).toBeTruthy();
    expect(screen.getByText("Current")).toBeTruthy();
  });

  test("renders available variables for the run as markdown", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Available Variables")).toBeTruthy();
    });
    expect(screen.getByText("handoff_note")).toBeTruthy();
    // The value is rendered through the markdown pipeline, so the heading and
    // emphasis produce real elements rather than literal "#" / "**" text.
    expect(screen.getByRole("heading", { name: "Handoff" })).toBeTruthy();
    expect(screen.getByText("it").tagName).toBe("STRONG");
  });

  test("opens a formatted session preview", async () => {
    renderComponent();

    await waitFor(() => {
      expect(screen.getByText("Available AI Sessions")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Preview"));

    await waitFor(() => {
      expect(loopRunService.getSessionPreview).toHaveBeenCalledWith(
        "test-run",
        "OpenCode",
        "ses_current",
      );
    });

    expect(screen.getByText("Session Preview")).toBeTruthy();
    expect(screen.getByText(/Messages: 1/)).toBeTruthy();
    expect(screen.getByText(/"role": "user"/)).toBeTruthy();
  });

  test("renders back link to loop runs overview", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText(/Back to all runs/)).toBeTruthy();
    });
  });

  test("renders run metadata with start time and execution count", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText(/Executions: 2/)).toBeTruthy();
    });
  });

  test("extracts and displays AI prompt from NodeStarted event payload", async () => {
    (loopRunService.getById as ReturnType<typeof vi.fn>).mockResolvedValue(mockRunWithAI);
    (loopRunService.getEvents as ReturnType<typeof vi.fn>).mockResolvedValue({
      entries: mockEventsWithAIInput,
      nextCursor: 1,
      hasMore: false,
    });
    (loopTemplateService.getVersionGraph as ReturnType<typeof vi.fn>).mockResolvedValue(
      mockTemplateGraphAI,
    );

    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run-ai/events"]}>
        <Routes>
          <Route path="/loop-runs/:runId/events" element={<EventLogViewer />} />
        </Routes>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("AI Node")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("AI Node"));
    await waitFor(() => {
      const elements = screen.getAllByText((content) => content.includes("Analyze Test WI"));
      expect(elements.length).toBeGreaterThanOrEqual(1);
    });
  });

  test("does not append context block to AI input display", async () => {
    (loopRunService.getById as ReturnType<typeof vi.fn>).mockResolvedValue(mockRunWithAI);
    (loopRunService.getEvents as ReturnType<typeof vi.fn>).mockResolvedValue({
      entries: mockEventsWithAIInput,
      nextCursor: 1,
      hasMore: false,
    });
    (loopTemplateService.getVersionGraph as ReturnType<typeof vi.fn>).mockResolvedValue(
      mockTemplateGraphAI,
    );

    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run-ai/events"]}>
        <Routes>
          <Route path="/loop-runs/:runId/events" element={<EventLogViewer />} />
        </Routes>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("AI Node")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("AI Node"));
    await waitFor(() => {
      expect(screen.queryByText(/--- Context ---/)).toBeNull();
    });
  });

  describe("multi-node expansion", () => {
    function renderMultiNodeViewer() {
      (loopRunService.getById as ReturnType<typeof vi.fn>).mockResolvedValue(
        mockRunWithMultipleNodes,
      );
      (loopRunService.getEvents as ReturnType<typeof vi.fn>).mockResolvedValue({
        entries: [],
        nextCursor: 0,
        hasMore: false,
      });
      (loopTemplateService.getVersionGraph as ReturnType<typeof vi.fn>).mockResolvedValue(
        mockTemplateGraphMulti,
      );

      render(
        <MemoryRouter initialEntries={["/loop-runs/test-run-multi/events"]}>
          <Routes>
            <Route path="/loop-runs/:runId/events" element={<EventLogViewer />} />
          </Routes>
        </MemoryRouter>,
      );
    }

    test("allows multiple nodes to be expanded simultaneously", async () => {
      renderMultiNodeViewer();

      await waitFor(() => {
        expect(screen.getByText("Start Node")).toBeTruthy();
        expect(screen.getByText("Cmd Node")).toBeTruthy();
      });

      // Cmd Node is Running so it auto-expands showing Live Output
      await waitFor(() => {
        expect(screen.getByText("Live Output")).toBeTruthy();
      });

      // Click Start Node to expand it too
      fireEvent.click(screen.getByText("Start Node"));
      await waitFor(() => {
        expect(screen.getByText("start output")).toBeTruthy();
      });

      // Cmd Node (running) should still be expanded
      expect(screen.getByText("Live Output")).toBeTruthy();
    });

    test("running node stays expanded when clicked", async () => {
      renderMultiNodeViewer();

      await waitFor(() => {
        expect(screen.getByText("Cmd Node")).toBeTruthy();
      });

      // Cmd Node is Running so it auto-expands showing Live Output
      await waitFor(() => {
        expect(screen.getByText("Live Output")).toBeTruthy();
      });

      // Click running node — should stay expanded (no-op)
      fireEvent.click(screen.getByText("Cmd Node"));
      await waitFor(() => {
        expect(screen.getByText("Live Output")).toBeTruthy();
      });
    });

    test("auto-expanding running node preserves other expanded nodes", async () => {
      renderMultiNodeViewer();

      await waitFor(() => {
        expect(screen.getByText("Start Node")).toBeTruthy();
      });

      // Cmd Node is Running so it auto-expands on mount
      await waitFor(() => {
        expect(screen.getByText("Live Output")).toBeTruthy();
      });

      // Expand Start Node
      fireEvent.click(screen.getByText("Start Node"));
      await waitFor(() => {
        expect(screen.getByText("start output")).toBeTruthy();
      });

      // Running node should still be expanded (not replaced)
      expect(screen.getByText("Live Output")).toBeTruthy();
    });
  });

  describe("edge labels", () => {
    const mockRunCustomEdge = {
      id: "test-run-custom",
      workItemId: "work-1",
      loopTemplateId: "template-1",
      templateVersion: 1,
      status: "Completed",
      currentNodeId: null,
      isPaused: false,
      nodeExecutionCount: 2,
      startedAt: "2025-01-01T00:00:00Z",
      completedAt: "2025-01-01T00:02:00Z",
      availableSessions: [],
      nodes: [
        {
          id: "rn-human",
          nodeId: "node-human",
          nodeLabel: "Ask Human",
          // Succeeded via the "Respond" outlet: the exact case where the old
          // guess-from-status logic mislabeled the edge as "custom".
          status: "Succeeded",
          output: "prompt",
          error: null,
          startedAt: "2025-01-01T00:00:00Z",
          completedAt: "2025-01-01T00:01:00Z",
          executionCount: 1,
        },
        {
          id: "rn-after",
          nodeId: "node-after",
          nodeLabel: "After Human",
          status: "Succeeded",
          output: "done",
          error: null,
          startedAt: "2025-01-01T00:01:00Z",
          completedAt: "2025-01-01T00:02:00Z",
          executionCount: 1,
          incomingEdgeId: "edge-respond",
        },
      ],
    };

    const mockGraphCustomEdge = {
      nodes: [
        { id: "node-human", type: "Human", label: "Ask Human", config: {}, maxTraversals: null },
        { id: "node-after", type: "Cmd", label: "After Human", config: {}, maxTraversals: null },
      ],
      edges: [
        {
          id: "edge-respond",
          sourceNodeId: "node-human",
          targetNodeId: "node-after",
          edgeType: "Custom",
          name: "Respond",
          maxTraversals: null,
        },
      ],
    };

    function renderViewer(runId: string) {
      render(
        <MemoryRouter initialEntries={[`/loop-runs/${runId}/events`]}>
          <Routes>
            <Route path="/loop-runs/:runId/events" element={<EventLogViewer />} />
          </Routes>
        </MemoryRouter>,
      );
    }

    test("labels a custom-edge routing by the edge name, not 'custom'", async () => {
      (loopRunService.getById as ReturnType<typeof vi.fn>).mockResolvedValue(mockRunCustomEdge);
      (loopRunService.getEvents as ReturnType<typeof vi.fn>).mockResolvedValue({
        entries: [],
        nextCursor: 0,
        hasMore: false,
      });
      (loopTemplateService.getVersionGraph as ReturnType<typeof vi.fn>).mockResolvedValue(
        mockGraphCustomEdge,
      );

      renderViewer("test-run-custom");

      await waitFor(() => {
        expect(screen.getByText("After Human")).toBeTruthy();
      });
      // The edge taken (the Human node's "Respond" outlet) is surfaced by name.
      expect(screen.getByText("Respond")).toBeTruthy();
      // The old guess-from-status behavior would have shown the generic role.
      expect(screen.queryByText("custom")).toBeNull();
    });

    test("falls back to the inferred role when the incoming edge is unknown", async () => {
      // A run predating IncomingEdgeId persistence: the second node carries no
      // incomingEdgeId, so the timeline infers from the failed predecessor.
      const legacyRun = {
        ...mockRunCustomEdge,
        id: "test-run-legacy",
        nodes: [
          { ...mockRunCustomEdge.nodes[0], status: "Failed", error: "boom" },
          { ...mockRunCustomEdge.nodes[1], incomingEdgeId: undefined },
        ],
      };
      (loopRunService.getById as ReturnType<typeof vi.fn>).mockResolvedValue(legacyRun);
      (loopRunService.getEvents as ReturnType<typeof vi.fn>).mockResolvedValue({
        entries: [],
        nextCursor: 0,
        hasMore: false,
      });
      (loopTemplateService.getVersionGraph as ReturnType<typeof vi.fn>).mockResolvedValue(
        mockGraphCustomEdge,
      );

      renderViewer("test-run-legacy");

      await waitFor(() => {
        expect(screen.getByText("After Human")).toBeTruthy();
      });
      expect(screen.getByText("failure")).toBeTruthy();
    });
  });
});
