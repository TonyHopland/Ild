import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType } from "../../types";
import LoopEditor from "./index";

// Spy on the React Flow viewport-fitting function. Must be prefixed with
// `mock` so vitest allows referencing it inside the hoisted vi.mock factory.
const mockFitView = vi.fn();

vi.mock("@xyflow/react", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@xyflow/react")>();
  const React = await import("react");
  return {
    ...actual,
    // A lightweight stand-in that exposes the real onInit/fitView contract: it
    // hands the editor an instance whose fitView we can assert on, and renders
    // each node's label so tests can tell which loop is currently open.
    ReactFlow: ({ nodes, children, onInit }: any) => {
      React.useEffect(() => {
        onInit?.({ fitView: mockFitView });
      }, [onInit]);
      return React.createElement(
        "div",
        { "data-testid": "react-flow" },
        ...(nodes ?? []).map((node: any) =>
          React.createElement("div", { key: node.id }, node.data?.label),
        ),
        children,
      );
    },
    Background: () => null,
    Controls: () => null,
    Panel: ({ children }: any) => React.createElement("div", null, children),
  };
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  mockFitView.mockClear();
});

const loopOne = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Loop One",
  description: "First loop",
  version: 1,
  nodes: [
    {
      id: "n-start-1",
      type: NodeType.Start,
      label: "Alpha Start",
      config: {},
      maxTraversals: null,
    },
    {
      id: "n-cleanup-1",
      type: NodeType.Cleanup,
      label: "Alpha Cleanup",
      config: {},
      maxTraversals: null,
    },
  ],
  edges: [
    {
      id: "e-1",
      sourceNodeId: "n-start-1",
      targetNodeId: "n-cleanup-1",
      edgeType: EdgeType.OnSuccess,
      maxTraversals: null,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
};

const loopTwo = {
  id: "22222222-2222-2222-2222-222222222222",
  name: "Loop Two",
  description: "Second loop",
  version: 1,
  nodes: [
    { id: "n-start-2", type: NodeType.Start, label: "Beta Start", config: {}, maxTraversals: null },
    {
      id: "n-cleanup-2",
      type: NodeType.Cleanup,
      label: "Beta Cleanup",
      config: {},
      maxTraversals: null,
    },
  ],
  edges: [
    {
      id: "e-2",
      sourceNodeId: "n-start-2",
      targetNodeId: "n-cleanup-2",
      edgeType: EdgeType.OnSuccess,
      maxTraversals: null,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
};

function stubFetch() {
  const fetchMock = vi.fn(async (url: string) => {
    const urlStr = String(url);
    if (urlStr.includes("/looptemplates") && !urlStr.includes("versions")) {
      return {
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify([loopOne, loopTwo])),
      };
    }
    if (urlStr.includes("/aiproviders") || urlStr.includes("/agentadapters")) {
      return { ok: true, status: 200, text: () => Promise.resolve(JSON.stringify([])) };
    }
    return { ok: true, status: 200, text: () => Promise.resolve(JSON.stringify([])) };
  });
  vi.stubGlobal("fetch", fetchMock);
}

function renderEditor() {
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

describe("Loop Editor fit view", () => {
  test("fits the view when a loop is opened in the editor", async () => {
    stubFetch();
    renderEditor();

    await waitFor(() => expect(screen.getByText("Loop One")).toBeTruthy());
    expect(mockFitView).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText("Loop One"));

    await waitFor(() => expect(screen.getByText("Alpha Start")).toBeTruthy());
    await waitFor(() => expect(mockFitView).toHaveBeenCalled());
  });

  test("re-fits the view when switching to a different loop", async () => {
    stubFetch();
    renderEditor();

    await waitFor(() => expect(screen.getByText("Loop One")).toBeTruthy());

    fireEvent.click(screen.getByText("Loop One"));
    await waitFor(() => expect(screen.getByText("Alpha Start")).toBeTruthy());
    await waitFor(() => expect(mockFitView).toHaveBeenCalled());

    mockFitView.mockClear();

    // Opening a second, different loop must trigger fit-view again — the bare
    // `fitView` prop only fits on the initial mount, so without the explicit
    // re-fit this second loop could be drawn outside the viewport.
    fireEvent.click(screen.getByText("Loop Two"));
    await waitFor(() => expect(screen.getByText("Beta Start")).toBeTruthy());
    await waitFor(() => expect(mockFitView).toHaveBeenCalled());
  });
});
