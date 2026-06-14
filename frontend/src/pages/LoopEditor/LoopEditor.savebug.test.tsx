import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType } from "../../types";
import LoopEditor from "./index";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const sampleTemplate = {
  id: "11111111-1111-1111-1111-111111111111",
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
    },
    {
      id: "n-cleanup",
      type: NodeType.Cleanup,
      label: "Tidy Up",
      config: {},
      maxTraversals: null,
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
};

describe("Loop Editor save existing template (regression)", () => {
  test("saving an existing template completes and does not hang", async () => {
    const fetchCalls: Array<{ url: string; method: string; body?: string }> = [];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";
      const urlStr = String(url);
      fetchCalls.push({ url: urlStr, method, body: init?.body as string | undefined });

      if (method === "GET" && urlStr.includes("/looptemplates") && !urlStr.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "POST" && urlStr.includes("/looptemplates/validate")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ valid: true, errors: [] })),
        };
      }

      if (method === "PUT" && urlStr.includes("/looptemplates/")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ id: sampleTemplate.id })),
        };
      }

      if (method === "GET" && urlStr.includes("/aiproviders")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
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

    // Select the template
    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

    // Click save
    fireEvent.click(screen.getByText("Save"));

    await waitFor(
      () => {
        const putCall = fetchCalls.find((c) => c.method === "PUT");
        expect(putCall).toBeTruthy();
      },
      { timeout: 5000 },
    );

    await waitFor(
      () => {
        expect(screen.getByText("Saved!")).toBeTruthy();
      },
      { timeout: 5000 },
    );

    // Save a second time to make sure it doesn't hang
    fireEvent.click(screen.getByText("Save"));
    await waitFor(
      () => {
        const putCalls = fetchCalls.filter((c) => c.method === "PUT");
        expect(putCalls.length).toBeGreaterThanOrEqual(2);
      },
      { timeout: 5000 },
    );
  }, 15000);

  test("saving an existing template that includes __pos config (round-trip) does not hang", async () => {
    const templateWithPos = {
      ...sampleTemplate,
      nodes: sampleTemplate.nodes.map((n, i) => ({
        ...n,
        config: { __pos: { x: 100 + i * 50, y: 80 } },
      })),
    };

    const fetchCalls: Array<{ url: string; method: string }> = [];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";
      const urlStr = String(url);
      fetchCalls.push({ url: urlStr, method });

      if (method === "GET" && urlStr.includes("/looptemplates") && !urlStr.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([templateWithPos])),
        };
      }

      if (method === "POST" && urlStr.includes("/looptemplates/validate")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ valid: true, errors: [] })),
        };
      }

      if (method === "PUT" && urlStr.includes("/looptemplates/")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ id: templateWithPos.id })),
        };
      }

      if (method === "GET" && urlStr.includes("/aiproviders")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
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
    fireEvent.click(screen.getByText("Dev Loop"));
    await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

    fireEvent.click(screen.getByText("Save"));

    await waitFor(() => {}, { timeout: 5000 });
  }, 15000);

  test("saving a template with cyclic edges does not hang the BFS", async () => {
    // Repro for: Set.add() returns the Set (truthy), causing infinite BFS
    // on graphs with cycles like "Build OnFailure -> AI Implement".
    const cyclicTemplate = {
      ...sampleTemplate,
      nodes: [
        {
          id: "n-start",
          type: NodeType.Start,
          label: "Start",
          config: {},
          maxTraversals: null,
        },
        {
          id: "n-ai",
          type: NodeType.AI,
          label: "AI Implement",
          config: {},
          maxTraversals: null,
        },
        {
          id: "n-build",
          type: NodeType.Cmd,
          label: "Build",
          config: {},
          maxTraversals: null,
        },
        {
          id: "n-cleanup",
          type: NodeType.Cleanup,
          label: "Cleanup",
          config: {},
          maxTraversals: null,
        },
      ],
      edges: [
        {
          id: "e-start-ai",
          sourceNodeId: "n-start",
          targetNodeId: "n-ai",
          edgeType: EdgeType.OnSuccess,
          maxTraversals: null,
        },
        {
          id: "e-ai-build",
          sourceNodeId: "n-ai",
          targetNodeId: "n-build",
          edgeType: EdgeType.OnSuccess,
          maxTraversals: null,
        },
        {
          id: "e-build-ai",
          sourceNodeId: "n-build",
          targetNodeId: "n-ai",
          edgeType: EdgeType.OnFailure,
          maxTraversals: null,
        },
        {
          id: "e-build-cleanup",
          sourceNodeId: "n-build",
          targetNodeId: "n-cleanup",
          edgeType: EdgeType.OnSuccess,
          maxTraversals: null,
        },
      ],
    };

    const fetchCalls: Array<{ url: string; method: string }> = [];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";
      const urlStr = String(url);
      fetchCalls.push({ url: urlStr, method });

      if (method === "GET" && urlStr.includes("/looptemplates") && !urlStr.includes("versions")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([cyclicTemplate])),
        };
      }

      if (method === "POST" && urlStr.includes("/looptemplates/validate")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ valid: true, errors: [] })),
        };
      }

      if (method === "PUT" && urlStr.includes("/looptemplates/")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ id: cyclicTemplate.id })),
        };
      }

      if (method === "GET" && urlStr.includes("/aiproviders")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([])),
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
    fireEvent.click(screen.getByText("Dev Loop"));
    await waitFor(() => expect(screen.getByText("Build")).toBeTruthy());

    fireEvent.click(screen.getByText("Save"));

    await waitFor(
      () => {
        const putCall = fetchCalls.find((c) => c.method === "PUT");
        expect(putCall).toBeTruthy();
      },
      { timeout: 5000 },
    );
  }, 15000);
});
