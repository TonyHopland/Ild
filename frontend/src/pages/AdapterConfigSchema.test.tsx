import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../hooks/useAuth";
import { NodeType, EdgeType, ConfigFieldType } from "../types";
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

function renderPage(fetchMock: ReturnType<typeof vi.fn>) {
  vi.stubGlobal("fetch", fetchMock);

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
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
      maxTraversals: null,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
};

const sampleSchema = [
  {
    name: "model",
    type: ConfigFieldType.Text,
    label: "Model",
    required: true,
    defaultValue: "gpt-4o",
    description: "Model identifier",
    options: null,
  },
  {
    name: "temperature",
    type: ConfigFieldType.Number,
    label: "Temperature",
    required: false,
    defaultValue: 0.7,
    description: "Controls randomness",
    options: null,
  },
];

describe("Adapter config schema", () => {
  test("clicking an AI node fetches the adapter config schema", async () => {
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

      if (method === "GET" && url.includes("AgentAdapters")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleSchema)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    renderPage(trackingFetch);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Click the template to load the graph
    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // Click the AI node to trigger schema fetch
    fireEvent.click(screen.getByText("Code"));

    // The schema endpoint should have been called
    await waitFor(() => {
      const schemaCall = fetchCalls.find(
        (c) => c.method === "GET" && c.url.includes("AgentAdapters"),
      );
      expect(schemaCall).toBeTruthy();
    });
  });

  test("adapter config fields render in AI node config panel after schema fetch", async () => {
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

      if (method === "GET" && url.includes("AgentAdapters")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleSchema)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    renderPage(trackingFetch);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Click the template to load the graph
    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // Click the AI node
    fireEvent.click(screen.getByText("Code"));

    // Wait for schema to be fetched and adapter config fields to render
    await waitFor(() => {
      expect(screen.getByText("Model")).toBeTruthy();
    });

    // Temperature field should also be visible
    expect(screen.getByText("Temperature")).toBeTruthy();
  });

  test("changing AI provider re-fetches schema for new provider type", async () => {
    const sampleProviders = [
      {
        id: "prov-1",
        name: "My OpenAI",
        type: "openai",
        baseUrl: "https://api.openai.com",
        apiKey: "",
        model: "gpt-4o",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "prov-2",
        name: "My Anthropic",
        type: "anthropic",
        baseUrl: "https://api.anthropic.com",
        apiKey: "",
        model: "claude-3",
        isDefault: false,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const anthropicSchema = [
      {
        name: "model",
        type: ConfigFieldType.Text,
        label: "Model",
        required: true,
        defaultValue: "claude-3",
        description: "Model identifier",
        options: null,
      },
    ];

    const schemaCalls: string[] = [];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";

      if (method === "GET" && url.includes("looptemplates")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "GET" && url.includes("aiproviders")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleProviders)),
        };
      }

      if (method === "GET" && url.includes("AgentAdapters")) {
        schemaCalls.push(url);
        if (url.includes("anthropic")) {
          return {
            ok: true,
            status: 200,
            text: () => Promise.resolve(JSON.stringify(anthropicSchema)),
          };
        }
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleSchema)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    renderPage(trackingFetch);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Click the template to load the graph
    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // Click the AI node
    fireEvent.click(screen.getByText("Code"));

    // Wait for initial schema to load
    await waitFor(() => {
      expect(screen.getByText("Model")).toBeTruthy();
    });

    // Change provider to Anthropic
    const select = screen.getByLabelText("AI Provider");
    fireEvent.change(select, { target: { value: "prov-2" } });

    // Verify a schema fetch was made for the anthropic type
    await waitFor(() => {
      const anthropicCall = schemaCalls.find((c) => c.includes("anthropic"));
      expect(anthropicCall).toBeTruthy();
    });
  });

  test("AI provider dropdown populates from API", async () => {
    const sampleProviders = [
      {
        id: "prov-1",
        name: "My OpenAI",
        type: "openai",
        baseUrl: "https://api.openai.com",
        apiKey: "",
        model: "gpt-4o",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";

      if (method === "GET" && url.includes("looptemplates")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "GET" && url.includes("aiproviders")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleProviders)),
        };
      }

      if (method === "GET" && url.includes("AgentAdapters")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleSchema)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    renderPage(trackingFetch);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Click the template to load the graph
    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // Click the AI node
    fireEvent.click(screen.getByText("Code"));

    // The AI provider dropdown should show "My OpenAI"
    await waitFor(() => {
      const select = screen.getByLabelText("AI Provider");
      expect(select).toBeTruthy();
      const options = (select as HTMLSelectElement).options;
      const providerLabels = Array.from(options).map((o) => o.label);
      expect(providerLabels).toContain("My OpenAI");
    });
  });

  test("changing an adapter config field updates the node config", async () => {
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

      if (method === "GET" && url.includes("AgentAdapters")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleSchema)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    renderPage(trackingFetch);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Click the template to load the graph
    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // Click the AI node
    fireEvent.click(screen.getByText("Code"));

    // Wait for schema fields to render
    await waitFor(() => {
      expect(screen.getByText("Model")).toBeTruthy();
    });

    // Change the model field
    const modelInput = screen.getByLabelText("Model");
    fireEvent.change(modelInput, { target: { value: "claude-3" } });

    // Verify the value changed
    expect((modelInput as HTMLInputElement).value).toBe("claude-3");
  });

  test("adapter config values serialize to JSON blob on save", async () => {
    const sampleProviders = [
      {
        id: "prov-1",
        name: "My OpenAI",
        type: "openai",
        baseUrl: "https://api.openai.com",
        apiKey: "",
        model: "gpt-4o",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    let savedPayload: unknown = null;

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";

      if (method === "GET" && url.includes("looptemplates")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([sampleTemplate])),
        };
      }

      if (method === "GET" && url.includes("aiproviders")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleProviders)),
        };
      }

      if (method === "GET" && url.includes("AgentAdapters")) {
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleSchema)),
        };
      }

      if (method === "POST" && url.includes("looptemplates/validate")) {
        const body = JSON.parse((init?.body as string) || "{}");
        // Verify the AI node's config includes adapterConfig
        const aiNode = body.nodes.find((n: { type: string }) => n.type === "AI");
        expect(aiNode).toBeTruthy();
        expect(aiNode.config.adapterConfig).toBeTruthy();
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify({ valid: true })),
        };
      }

      if (method === "PUT" && url.includes("looptemplates")) {
        savedPayload = JSON.parse((init?.body as string) || "{}");
        return {
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(sampleTemplate)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    renderPage(trackingFetch);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    // Click the template to load the graph
    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    // Click the AI node
    fireEvent.click(screen.getByText("Code"));

    // Wait for schema fields to render
    await waitFor(() => {
      expect(screen.getByText("Model")).toBeTruthy();
    });

    // Change the model field
    const modelInput = screen.getByLabelText("Model");
    fireEvent.change(modelInput, { target: { value: "claude-3" } });

    // Click save
    fireEvent.click(screen.getByText("Save"));

    // Wait for save to complete
    await waitFor(() => {
      expect(savedPayload).not.toBeNull();
    });

    // Verify the saved payload contains adapter config
    const savedNodes = (
      savedPayload as { nodes: Array<{ type: string; config: Record<string, unknown> }> }
    ).nodes;
    const aiNode = savedNodes.find((n) => n.type === "AI");
    expect(aiNode).toBeTruthy();
    expect(aiNode!.config.adapterConfig).toBeTruthy();
    expect((aiNode!.config.adapterConfig as Record<string, unknown>).model).toBe("claude-3");
  });
});
