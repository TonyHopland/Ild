import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../hooks/useAuth";
import { NodeType, EdgeType } from "../types";
import LoopEditor from "./LoopEditor";

beforeEach(() => {
  vi.stubGlobal(
    "ResizeObserver",
    class ResizeObserver {
      private callback: ResizeObserverCallback | null = null;
      constructor(callback: ResizeObserverCallback) {
        this.callback = callback;
      }
      observe() {
        if (this.callback) {
          this.callback([], this);
        }
      }
      unobserve() {}
      disconnect() {}
    },
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function mockFetch(json: unknown, status = 200) {
  return vi.fn().mockResolvedValue({
    ok: status < 400,
    status,
    text: () => Promise.resolve(JSON.stringify(json)),
  });
}

function renderPage(mockFetchFn: ReturnType<typeof mockFetch>) {
  vi.stubGlobal("fetch", mockFetchFn);

  const authValue = {
    user: { id: "1", username: "test", createdAt: "" },
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  };

  render(
    <MemoryRouter>
      <AuthContext.Provider value={authValue}>
        <LoopEditor />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

const sampleTemplate = {
  id: "tpl-1",
  name: "Dev Loop",
  description: "Standard development loop",
  version: 3,
  nodes: [
    {
      id: "n-start",
      type: NodeType.Start,
      label: "Initialize",
      config: {},
      maxTraversals: null,
      retryCount: null,
      timeoutSeconds: null,
    },
    {
      id: "n-ai",
      type: NodeType.AI,
      label: "Code",
      config: {},
      maxTraversals: null,
      retryCount: null,
      timeoutSeconds: null,
    },
    {
      id: "n-cmd",
      type: NodeType.Cmd,
      label: "Test",
      config: {},
      maxTraversals: null,
      retryCount: null,
      timeoutSeconds: null,
    },
    {
      id: "n-cleanup",
      type: NodeType.Cleanup,
      label: "Tidy Up",
      config: {},
      maxTraversals: null,
      retryCount: null,
      timeoutSeconds: null,
    },
  ],
  edges: [
    {
      id: "e-1",
      sourceNodeId: "n-start",
      targetNodeId: "n-ai",
      edgeType: EdgeType.OnSuccess,
      maxTraversals: null,
    },
    {
      id: "e-2",
      sourceNodeId: "n-ai",
      targetNodeId: "n-cmd",
      edgeType: EdgeType.OnSuccess,
      maxTraversals: null,
    },
    {
      id: "e-3",
      sourceNodeId: "n-cmd",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
      maxTraversals: null,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
};

describe("Loop Editor canvas", () => {
  test("selecting a template renders its nodes on the canvas", async () => {
    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Click the template to load its graph
    fireEvent.click(screen.getByText("Dev Loop"));

    // Canvas should render nodes with their labels
    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    expect(screen.getByText("Initialize")).toBeTruthy();
    expect(screen.getByText("Code")).toBeTruthy();
    expect(screen.getByText("Test")).toBeTruthy();
    expect(screen.getByText("Tidy Up")).toBeTruthy();
  });

  test("canvas shows correct node count matching template", async () => {
    const templateWithAllTypes = {
      ...sampleTemplate,
      nodes: [
        {
          id: "n-start",
          type: NodeType.Start,
          label: "Start Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
          timeoutSeconds: null,
        },
        {
          id: "n-cmd",
          type: NodeType.Cmd,
          label: "Cmd Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
          timeoutSeconds: null,
        },
        {
          id: "n-ai",
          type: NodeType.AI,
          label: "AI Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
          timeoutSeconds: null,
        },
        {
          id: "n-human",
          type: NodeType.Human,
          label: "Human Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
          timeoutSeconds: null,
        },
        {
          id: "n-pr",
          type: NodeType.PR,
          label: "PR Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
          timeoutSeconds: null,
        },
        {
          id: "n-cleanup",
          type: NodeType.Cleanup,
          label: "Cleanup Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
          timeoutSeconds: null,
        },
      ],
    };

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify([templateWithAllTypes])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Dev Loop"));

    // All 6 node labels should appear
    await waitFor(() => {
      expect(screen.getByText("Start Node")).toBeTruthy();
    });

    expect(screen.getByText("Start Node")).toBeTruthy();
    expect(screen.getByText("Cmd Node")).toBeTruthy();
    expect(screen.getByText("AI Node")).toBeTruthy();
    expect(screen.getByText("Human Node")).toBeTruthy();
    expect(screen.getByText("PR Node")).toBeTruthy();
    expect(screen.getByText("Cleanup Node")).toBeTruthy();
  });

  test("each node type has distinct visual styling", async () => {
    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // Each node should have a type badge showing its type
    expect(screen.getByText(NodeType.Start)).toBeTruthy();
    expect(screen.getByText(NodeType.AI)).toBeTruthy();
    expect(screen.getByText(NodeType.Cmd)).toBeTruthy();
    expect(screen.getByText(NodeType.Cleanup)).toBeTruthy();
  });

  test("edges are rendered between nodes", async () => {
    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // The edges container should exist within the canvas
    const edgesContainer = document.querySelector(".react-flow__edges");
    expect(edgesContainer).toBeTruthy();
  });

  test("on-success and on-failure edges are visually distinguished", async () => {
    const { templateToEdges } = await import("../utils/loopGraphConverter");

    const templateWithBothEdgeTypes = {
      ...sampleTemplate,
      edges: [
        {
          id: "e-1",
          sourceNodeId: "n-start",
          targetNodeId: "n-ai",
          edgeType: EdgeType.OnSuccess,
          maxTraversals: null,
        },
        {
          id: "e-2",
          sourceNodeId: "n-ai",
          targetNodeId: "n-start",
          edgeType: EdgeType.OnFailure,
          maxTraversals: null,
        },
      ],
    };

    const edges = templateToEdges(templateWithBothEdgeTypes);
    expect(edges.length).toBe(2);

    // On-success edge should not have dasharray
    const successEdge = edges.find((e) => e.id === "e-1");
    expect(successEdge?.style?.stroke).toBe("#10b981");
    expect(successEdge?.style?.strokeDasharray).toBeUndefined();

    // On-failure edge should have dasharray and different color
    const failureEdge = edges.find((e) => e.id === "e-2");
    expect(failureEdge?.style?.stroke).toBe("#ef4444");
    expect(failureEdge?.style?.strokeDasharray).toBe("8 4");
  });
});
