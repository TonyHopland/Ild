import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";

vi.mock("../services/auth", () => ({
  loopRunService: {
    getById: vi.fn(),
    getEvents: vi.fn(),
    getPayload: vi.fn(),
    cancel: vi.fn(),
    pause: vi.fn(),
    resume: vi.fn(),
  },
  loopTemplateService: {
    getVersionGraph: vi.fn(),
  },
}));

vi.mock("../hooks/useSignalR", () => ({
  useSignalR: vi.fn().mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
    connectionState: "connected",
  }),
}));

import EventLogViewer from "./EventLogViewer";
import { loopRunService, loopTemplateService } from "../services/auth";

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
      retryCount: null,
      timeoutSeconds: null,
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
      retryCount: null,
      timeoutSeconds: null,
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
});
