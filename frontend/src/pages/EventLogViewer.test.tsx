import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
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
  },
  {
    sequence: 2,
    runId: "test-run",
    eventType: "NodeCompleted",
    nodeId: "node-1",
    payload: "Second event message",
    timestamp: "2025-01-01T00:01:00Z",
    hasPayload: true,
  },
];

const mockPage = {
  entries: mockEvents,
  nextCursor: 2,
  hasMore: true,
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
      output: null,
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

  test("renders event list with sequence, type badge, timestamp, and message", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("#1")).toBeTruthy();
    });
    expect(screen.getByText("NodeStarted")).toBeTruthy();
    expect(screen.getByText("NodeCompleted")).toBeTruthy();
    expect(screen.getByText("First event message")).toBeTruthy();
  });

  test("expands event to show full payload on click", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("#1")).toBeTruthy();
    });
    const eventItem = screen.getByText("#1").closest(".event-item");
    fireEvent.click(eventItem!);
    expect(eventItem?.classList.contains("expanded")).toBe(true);
  });

  test("shows Load More button when hasMore is true", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Load More")).toBeTruthy();
    });
  });

  test("fetches next page when Load More is clicked", async () => {
    const mockPage2 = {
      entries: [
        {
          sequence: 3,
          runId: "test-run",
          eventType: "NodeStarted",
          nodeId: null,
          payload: "Third event",
          timestamp: "2025-01-01T00:02:00Z",
          hasPayload: false,
        },
      ],
      nextCursor: 3,
      hasMore: false,
    };

    (loopRunService.getEvents as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce(mockPage)
      .mockResolvedValueOnce(mockPage2);

    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Load More")).toBeTruthy();
    });
    fireEvent.click(screen.getByText("Load More"));
    await waitFor(() => {
      expect(screen.getByText("Third event")).toBeTruthy();
    });
  });

  test("shows Load Payload button for events with disk-stored payloads", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("#2")).toBeTruthy();
    });
    const eventItem = screen.getByText("#2").closest(".event-item");
    fireEvent.click(eventItem!);
    expect(screen.getByText("Load Payload")).toBeTruthy();
  });

  test("loads and displays payload when Load Payload is clicked", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("#2")).toBeTruthy();
    });
    const eventItem = screen.getByText("#2").closest(".event-item");
    fireEvent.click(eventItem!);
    fireEvent.click(screen.getByText("Load Payload"));
    await waitFor(() => {
      expect(screen.getByText("large payload content")).toBeTruthy();
    });
  });

  test("renders node flow panel with template nodes", async () => {
    renderComponent();
    await waitFor(() => {
      expect(screen.getByText("Node Flow")).toBeTruthy();
    });
    const flowPanel = screen.getByText("Node Flow").closest(".node-flow-panel");
    expect(flowPanel?.textContent).toContain("Start Node");
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
});
