import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../hooks/useAuth";
import { NodeType, EdgeType, ConfigFieldType } from "../types";
import LoopEditor from "./LoopEditor";

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
    <MemoryRouter initialEntries={["/loop-editor"]}>
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor" element={<LoopEditor />} />
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
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
    },
    {
      id: "n-ai",
      type: NodeType.AI,
      label: "Code",
      config: {},
    },
    {
      id: "n-cleanup",
      type: NodeType.Cleanup,
      label: "Tidy Up",
      config: {},
    },
  ],
  edges: [
    {
      id: "e-1",
      sourceNodeId: "n-start",
      targetNodeId: "n-ai",
      edgeType: EdgeType.OnSuccess,
    },
    {
      id: "e-2",
      sourceNodeId: "n-ai",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
};

const sampleSchema = [
  {
    name: "temperature",
    type: ConfigFieldType.Number,
    label: "Temperature",
    required: false,
    defaultValue: 0.7,
    description: "Controls randomness in output",
    options: null,
  },
  {
    name: "maxTokens",
    type: ConfigFieldType.Number,
    label: "Max Tokens",
    required: false,
    defaultValue: 4096,
    description: "Maximum tokens in the response",
    options: null,
  },
];

const sampleProviders = [
  {
    id: "prov-1",
    name: "My Pi",
    type: "pi",
    baseUrl: "http://pi.local",
    apiKey: "",
    model: "gpt-4o",
    isDefault: true,
    createdAt: "2025-01-01T00:00:00Z",
  },
];

describe("Adapter config schema", () => {
  test("clicking an AI node on the default provider fetches the adapter config schema", async () => {
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

    // The node runs on the default provider, so its schema is fetched on open
    await waitFor(() => {
      const schemaCall = fetchCalls.find(
        (c) => c.method === "GET" && c.url.includes("AgentAdapters"),
      );
      expect(schemaCall).toBeTruthy();
    });
  });

  test("adapter config fields render for the provider the node runs on", async () => {
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

    // Wait for the default provider's schema to be fetched and adapter config fields to render
    await waitFor(() => {
      expect(screen.getByText("Temperature")).toBeTruthy();
    });

    // Max Tokens field should also be visible
    expect(screen.getByText("Max Tokens")).toBeTruthy();
  });

  test("no adapter config fields shown when no provider resolves", async () => {
    const noDefault = sampleProviders.map((provider) => ({ ...provider, isDefault: false }));
    const schemaCalls: string[] = [];

    const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
      const method = (init?.method as string) ?? "GET";
      if (url.includes("AgentAdapters")) schemaCalls.push(url);

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
          text: () => Promise.resolve(JSON.stringify(noDefault)),
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

    // No tag and no default provider: nothing to run on, so no fields to offer.
    expect(await screen.findByText(/no default provider is configured/i)).toBeTruthy();
    expect(screen.queryByText("Temperature")).toBeFalsy();
    expect(schemaCalls).toEqual([]);
  });

  test("a tag held by another provider re-fetches schema for its type", async () => {
    const sampleProvidersMulti = [
      {
        id: "prov-1",
        name: "My Pi",
        type: "pi",
        baseUrl: "http://pi.local",
        apiKey: "",
        model: "gpt-4o",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "prov-2",
        name: "My OpenCode",
        type: "opencode",
        baseUrl: "http://opencode.local",
        apiKey: "",
        model: "claude-3",
        isDefault: false,
        tags: ["Review"],
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const anthropicSchema = [
      {
        name: "temperature",
        type: ConfigFieldType.Number,
        label: "Temperature",
        required: false,
        defaultValue: 0.5,
        description: "Controls randomness",
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
          text: () => Promise.resolve(JSON.stringify(sampleProvidersMulti)),
        };
      }

      if (method === "GET" && url.includes("AgentAdapters")) {
        schemaCalls.push(url);
        if (url.includes("opencode")) {
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

    // Tag the node with OpenCode's tag
    fireEvent.change(screen.getByLabelText("Provider tag"), { target: { value: "Review" } });

    // Verify a schema fetch was made for the opencode type
    await waitFor(() => {
      const opencodeCall = schemaCalls.find((c) => c.includes("opencode"));
      expect(opencodeCall).toBeTruthy();
    });
  });

  test("changing an adapter config field updates the value", async () => {
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

    // Wait for schema fields to render
    await waitFor(() => {
      expect(screen.getByText("Temperature")).toBeTruthy();
    });

    // Change the temperature field
    const tempInput = screen.getByLabelText("Temperature");
    fireEvent.change(tempInput, { target: { value: "0.1" } });

    // Verify the value changed
    expect((tempInput as HTMLInputElement).value).toBe("0.1");
  });

  test("adapter config values serialize to JSON blob on save", async () => {
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
      expect(screen.getByText("Temperature")).toBeTruthy();
    });

    // Change the temperature field
    const tempInput = screen.getByLabelText("Temperature");
    fireEvent.change(tempInput, { target: { value: "0.1" } });

    // Save node settings first
    const nodeSettingsSaveBtn = document.querySelector(".node-settings-btn-save");
    fireEvent.click(nodeSettingsSaveBtn!);

    // Wait for node settings to close
    await waitFor(() => {
      expect(document.querySelector(".node-settings-modal")).toBeFalsy();
    });

    // Click the header save button — this opens the review-diff modal; confirm it.
    const saveBtn = document.querySelector(".save-btn");
    fireEvent.click(saveBtn!);
    fireEvent.click(await screen.findByText("Save changes"));

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
    expect((aiNode!.config.adapterConfig as Record<string, unknown>).temperature).toBe(0.1);
  });

  test("the provider-scoped Custom MCP servers field is excluded from the node editor", async () => {
    // The adapter advertises the MCP field in its schema (it is rendered on the
    // AI Providers page), but a node's AdapterConfig is never read at run time, so
    // the Loop Editor must filter it out while still rendering genuinely
    // node-scoped fields.
    const schemaWithMcp = [
      ...sampleSchema,
      {
        name: "customMcpServersJson",
        type: ConfigFieldType.Textarea,
        label: "Custom MCP servers (JSON)",
        required: false,
        defaultValue: null,
        description: "Optional MCP servers.",
        options: null,
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
          text: () => Promise.resolve(JSON.stringify(schemaWithMcp)),
        };
      }

      return { ok: false, status: 500, text: () => Promise.resolve("") };
    });

    renderPage(trackingFetch);

    await waitFor(() => {
      expect(screen.getByText("Dev Loop")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Dev Loop"));

    await waitFor(() => {
      expect(screen.getByText("Initialize")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Code"));

    // A genuinely node-scoped field renders…
    await waitFor(() => {
      expect(screen.getByText("Temperature")).toBeTruthy();
    });
    // …but the provider-scoped MCP field is filtered out here.
    expect(screen.queryByText("Custom MCP servers (JSON)")).toBeFalsy();
  });
});
