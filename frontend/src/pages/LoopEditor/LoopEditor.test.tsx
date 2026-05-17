import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import type { Edge } from "@xyflow/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType, RecoveryPolicy } from "../../types";
import LoopEditor from "./index";

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
  recoveryPolicy: RecoveryPolicy.AutoResume,
  maxNodeExecutions: 200,
  maxWallClockHours: 24,
  nodes: [
    {
      id: "n-start",
      type: NodeType.Start,
      label: "Initialize",
      config: {},
      maxTraversals: null,
      retryCount: null,
    },
    {
      id: "n-ai",
      type: NodeType.AI,
      label: "Code",
      config: {},
      maxTraversals: null,
      retryCount: null,
    },
    {
      id: "n-cmd",
      type: NodeType.Cmd,
      label: "Test",
      config: {},
      maxTraversals: null,
      retryCount: null,
    },
    {
      id: "n-cleanup",
      type: NodeType.Cleanup,
      label: "Tidy Up",
      config: {},
      maxTraversals: null,
      retryCount: null,
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
  isArchived: false,
};

describe("Loop Editor canvas", () => {
  test("collapsing the loop menu keeps the drag palette visible", async () => {
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

    expect(screen.getByText("Drag & Drop")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Collapse loop menu" }));

    expect(screen.queryByText("Dev Loop")).toBeNull();
    expect(screen.getByText("Drag & Drop")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Expand loop menu" })).toBeTruthy();
  });

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
        },
        {
          id: "n-cmd",
          type: NodeType.Cmd,
          label: "Cmd Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
        },
        {
          id: "n-ai",
          type: NodeType.AI,
          label: "AI Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
        },
        {
          id: "n-human",
          type: NodeType.Human,
          label: "Human Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
        },
        {
          id: "n-pr",
          type: NodeType.PR,
          label: "PR Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
        },
        {
          id: "n-cleanup",
          type: NodeType.Cleanup,
          label: "Cleanup Node",
          config: {},
          maxTraversals: null,
          retryCount: null,
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

    // Each node should have a type badge showing its type (at least 2: palette + node badge)
    expect(screen.getAllByText(NodeType.Start).length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText(NodeType.AI).length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText(NodeType.Cmd).length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText(NodeType.Cleanup).length).toBeGreaterThanOrEqual(2);
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
    const { templateToEdges } = await import("../../utils/loopGraphConverter");

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
    const successEdge = edges.find((edge: Edge) => edge.id === "e-1");
    expect(successEdge?.style?.stroke).toBe("#10b981");
    expect(successEdge?.style?.strokeDasharray).toBeUndefined();

    // On-failure edge should have dasharray and different color
    const failureEdge = edges.find((edge: Edge) => edge.id === "e-2");
    expect(failureEdge?.style?.stroke).toBe("#ef4444");
    expect(failureEdge?.style?.strokeDasharray).toBe("8 4");
  });
});

describe("Loop Editor template clone", () => {
  test("clicking clone sends POST to clone endpoint with new name", async () => {
    const fetchCalls: Array<{ url: string; method: string }> = [];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";
      fetchCalls.push({ url: String(url), method });

      if (method === "GET" && url.includes("looptemplates")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "POST" && url.includes("clone")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ id: "cloned-template-id" })),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    vi.stubGlobal("fetch", trackingFetch);

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

    await waitFor(() => expect(screen.getByText("Dev Loop")).toBeTruthy());

    // Click clone button on the template
    fireEvent.click(screen.getByText("Clone"));

    // Enter a new name in the inline input
    const cloneInput = screen.getByPlaceholderText(/clone name/i);
    fireEvent.change(cloneInput, { target: { value: "My Cloned Template" } });

    // Confirm clone
    fireEvent.click(screen.getByText("Clone"));

    await waitFor(() => {
      const cloneCall = fetchCalls.find((c) => c.method === "POST" && c.url.includes("clone"));
      expect(cloneCall).toBeTruthy();
      expect(cloneCall!.url).toContain(sampleTemplate.id);
      expect(cloneCall!.url).toContain("newName=");
    });

    vi.unstubAllGlobals();
  });
});

describe("Loop Editor version history", () => {
  test("clicking versions button fetches version history and displays list", async () => {
    const fetchCalls: Array<{ url: string; method: string }> = [];
    const sampleVersions = [
      {
        id: "v1-id",
        loopTemplateId: sampleTemplate.id,
        versionNumber: 1,
        createdAt: "2025-01-01T00:00:00Z",
        nodeCount: 3,
        edgeCount: 2,
      },
      {
        id: "v2-id",
        loopTemplateId: sampleTemplate.id,
        versionNumber: 2,
        createdAt: "2025-02-01T00:00:00Z",
        nodeCount: 4,
        edgeCount: 3,
      },
    ];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";
      fetchCalls.push({ url: String(url), method });

      if (method === "GET" && url.includes("looptemplates") && !url.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "GET" && url.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleVersions)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    vi.stubGlobal("fetch", trackingFetch);

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

    await waitFor(() => expect(screen.getByText("Dev Loop")).toBeTruthy());

    // Click versions button
    fireEvent.click(screen.getByText("Versions"));

    // Verify versions API was called
    await waitFor(() => {
      const versionsCall = fetchCalls.find((c) => c.method === "GET" && c.url.includes("versions"));
      expect(versionsCall).toBeTruthy();
    });

    // Verify version history header is visible
    await waitFor(() => {
      expect(screen.getByText("Version History")).toBeTruthy();
    });

    // Verify version entries are displayed
    expect(screen.getByText("v1")).toBeTruthy();
    expect(screen.getByText("v2")).toBeTruthy();

    vi.unstubAllGlobals();
  });

  test("back button returns to template list", async () => {
    const sampleVersions = [
      {
        id: "v1-id",
        loopTemplateId: sampleTemplate.id,
        versionNumber: 1,
        createdAt: "2025-01-01T00:00:00Z",
        nodeCount: 3,
        edgeCount: 2,
      },
    ];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";

      if (method === "GET" && url.includes("looptemplates") && !url.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "GET" && url.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleVersions)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    vi.stubGlobal("fetch", trackingFetch);

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

    await waitFor(() => expect(screen.getByText("Dev Loop")).toBeTruthy());

    // Click versions button
    fireEvent.click(screen.getByText("Versions"));
    await waitFor(() => expect(screen.getByText("Version History")).toBeTruthy());

    // Click back button
    fireEvent.click(screen.getByText("← Back"));

    // Template list should be visible again
    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Version history header should no longer be visible
    expect(screen.queryByText("Version History")).toBeFalsy();

    vi.unstubAllGlobals();
  });
});

describe("Loop Editor read-only version inspection", () => {
  test("selecting a version shows read-only banner and disables save", async () => {
    const sampleVersions = [
      {
        id: "v1-id",
        loopTemplateId: sampleTemplate.id,
        versionNumber: 1,
        createdAt: "2025-01-01T00:00:00Z",
        nodeCount: 3,
        edgeCount: 2,
      },
    ];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";

      if (method === "GET" && url.includes("looptemplates") && !url.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "GET" && url.includes("versions") && !url.includes("versions/")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleVersions)),
        };
      }

      // When loading a specific version for inspection
      if (method === "GET" && url.includes("versions/1")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ ...sampleTemplate, version: 1 })),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    vi.stubGlobal("fetch", trackingFetch);

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

    await waitFor(() => expect(screen.getByText("Dev Loop")).toBeTruthy());

    // Open version history
    fireEvent.click(screen.getByText("Versions"));
    await waitFor(() => expect(screen.getByText("Version History")).toBeTruthy());

    // Select version 1
    fireEvent.click(screen.getByText("v1"));

    // Read-only banner should appear
    await waitFor(() => {
      expect(screen.getByText(/read-only/i)).toBeTruthy();
    });

    // Save button should not be visible in read-only mode
    expect(screen.queryByText("Save")).toBeFalsy();

    // Palette should be disabled (have the disabled class)
    const palette = document.querySelector(".node-palette");
    expect(palette?.classList.contains("palette-disabled")).toBe(true);

    vi.unstubAllGlobals();
  });

  test("clicking read-only banner exits read-only mode", async () => {
    const sampleVersions = [
      {
        id: "v1-id",
        loopTemplateId: sampleTemplate.id,
        versionNumber: 1,
        createdAt: "2025-01-01T00:00:00Z",
        nodeCount: 3,
        edgeCount: 2,
      },
    ];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";

      if (method === "GET" && url.includes("looptemplates") && !url.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "GET" && url.includes("versions") && !url.includes("versions/")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleVersions)),
        };
      }

      if (method === "GET" && url.includes("versions/1")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ ...sampleTemplate, version: 1 })),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    vi.stubGlobal("fetch", trackingFetch);

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

    await waitFor(() => expect(screen.getByText("Dev Loop")).toBeTruthy());

    // Open version history and select version
    fireEvent.click(screen.getByText("Versions"));
    await waitFor(() => expect(screen.getByText("Version History")).toBeTruthy());
    fireEvent.click(screen.getByText("v1"));
    await waitFor(() => expect(screen.getByText(/read-only/i)).toBeTruthy());

    // Click the banner to exit read-only mode
    fireEvent.click(screen.getByText(/read-only/i));

    // Banner should no longer be visible
    expect(screen.queryByText(/read-only/i)).toBeFalsy();

    // Palette should no longer be disabled
    const palette = document.querySelector(".node-palette");
    expect(palette?.classList.contains("palette-disabled")).toBe(false);

    vi.unstubAllGlobals();
  });
});

describe("Loop Editor responsive sidebar", () => {
  test("clicking sidebar toggle hides the sidebar panels", async () => {
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

    // Menu and palette should be visible initially
    expect(document.querySelector(".node-palette")).toBeTruthy();
    expect(document.querySelector(".loop-list")).toBeTruthy();

    // Click the menu toggle button
    const toggle = screen.getByRole("button", { name: /collapse loop menu/i });
    fireEvent.click(toggle);

    // The menu should hide but the palette stays beside the canvas
    await waitFor(() => {
      expect(document.querySelector(".node-palette")).toBeTruthy();
      expect(document.querySelector(".loop-list")).toBeFalsy();
    });
  });

  test("clicking sidebar toggle again shows the sidebar panels", async () => {
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

    // Hide menu
    const hideToggle = screen.getByRole("button", { name: /collapse loop menu/i });
    fireEvent.click(hideToggle);

    await waitFor(() => {
      expect(document.querySelector(".loop-list")).toBeFalsy();
    });

    // Show menu again from the left rail
    fireEvent.click(screen.getByRole("button", { name: /expand loop menu/i }));

    await waitFor(() => {
      expect(document.querySelector(".node-palette")).toBeTruthy();
      expect(document.querySelector(".loop-list")).toBeTruthy();
    });
  });

  test("header does not render a loop menu toggle", async () => {
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

    expect(screen.queryByRole("button", { name: /show loop menu/i })).toBeNull();
  });
});
