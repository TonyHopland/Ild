import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { ConfigFieldType } from "../../types";
import AiProviders from "./index";

afterEach(() => {
  cleanup();
});

// The MCP-capable adapters (opencode, claude-code) advertise a single
// "Custom MCP servers (JSON)" textarea via /AgentAdapters/{type}/config-schema.
const customMcpSchema = [
  {
    name: "customMcpServersJson",
    type: ConfigFieldType.Textarea,
    label: "Custom MCP servers (JSON)",
    required: false,
    defaultValue: null,
    description: "Optional. A JSON object mapping a server name to its definition.",
    options: null,
  },
];

// A URL-routed fetch mock: unlike the ordered queue above, it resolves each
// request by inspecting the URL/method, so the extra per-type config-schema
// fetch this feature adds can't desync the response order. `requests` records
// every call for assertions on the save payload.
function routingFetch(options: {
  providers?: unknown[];
  types?: string[];
  schema?: unknown[];
  agents?: unknown[];
  onWrite?: (url: string, init: RequestInit) => unknown;
  requests: Array<{ url: string; method: string; body: unknown }>;
}) {
  const { providers = [], types = [], schema = [], agents = [], onWrite, requests } = options;
  return vi.fn(async (url: string, init?: RequestInit) => {
    const method = (init?.method as string) ?? "GET";
    const body = init?.body ? JSON.parse(init.body as string) : undefined;
    requests.push({ url, method, body });
    const ok = (json: unknown, status = 200) => ({
      ok: status < 400,
      status,
      text: () => Promise.resolve(JSON.stringify(json)),
    });

    if (method === "GET" && url.includes("config-schema")) return ok(schema);
    if (method === "GET" && url.includes("AgentAdapters")) return ok(types);
    if (method === "GET" && url.includes("managedagents")) return ok(agents);
    if (method === "GET" && url.includes("aiproviders")) return ok(providers);
    if (method === "POST" || method === "PUT") return ok(onWrite?.(url, init!) ?? {});
    return ok(null);
  });
}

function renderRouted(fetchMock: ReturnType<typeof routingFetch>) {
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
        <AiProviders />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

function mockFetch(json: unknown, status = 200) {
  return vi.fn().mockResolvedValue({
    ok: status < 400,
    status,
    text: () => Promise.resolve(JSON.stringify(json)),
  });
}

function jsonResponse(json: unknown, status = 200) {
  return Promise.resolve({
    ok: status < 400,
    status,
    text: () => Promise.resolve(JSON.stringify(json)),
  });
}

// The page loads three resources on mount, in this fetch order:
// providers, supported types, managed agents.
function queueInitialLoad(
  fetchMock: ReturnType<typeof mockFetch>,
  providers: unknown[],
  agents: unknown[] = [],
) {
  fetchMock
    .mockReturnValueOnce(jsonResponse(providers))
    .mockReturnValueOnce(jsonResponse(["opencode", "pi"]))
    .mockReturnValueOnce(jsonResponse(agents));
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
        <AiProviders />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

// Coding agents now live behind a notification bell in the header; their
// version details and install/update actions are only rendered once the
// popover is opened.
async function openAgentsPopover() {
  const bell = await screen.findByRole("button", { name: /Coding agents/i });
  fireEvent.click(bell);
}

describe("AI Providers page", () => {
  test("renders provider list with name, type, URL, and model", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "Pi",
        type: "pi",
        baseUrl: "http://pi.local",
        apiKey: "sk-secret",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "ai-2",
        name: "OpenCode",
        type: "opencode",
        baseUrl: "http://opencode.local",
        apiKey: "sk-secret-2",
        model: "claude-3",
        isDefault: false,
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(providers)),
      }),
    );
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    expect(screen.getByText("gpt-4")).toBeTruthy();
    expect(screen.getByText("claude-3")).toBeTruthy();
    expect(screen.getByText("http://pi.local")).toBeTruthy();
  });

  test("API key is masked in the list view", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "Pi",
        type: "pi",
        baseUrl: "http://pi.local",
        apiKey: "sk-super-secret-key",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(providers)),
      }),
    );
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    expect(screen.queryByText("sk-super-secret-key")).toBeFalsy();
    expect(screen.getByText("••••••••")).toBeTruthy();
  });

  test("create form opens, fills fields, and calls API on submit", async () => {
    const providers: unknown[] = [];

    const createdProvider = {
      id: "ai-new-1",
      name: "Pi Local",
      type: "pi",
      baseUrl: "https://generativelanguage.googleapis.com",
      apiKey: "new-key",
      model: "gemini-pro",
      isDefault: false,
      createdAt: "2025-03-01T00:00:00Z",
    };

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(providers)),
      }),
    );
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    // Open create form
    fireEvent.click(screen.getByText("+ New Provider"));
    await waitFor(() => {
      expect(screen.getByText("New Provider")).toBeTruthy();
    });

    // Fill in required fields
    fireEvent.change(screen.getByLabelText("Name"), {
      target: { value: "Pi Local" },
    });

    const typeSelect = screen.getByLabelText("Type");
    fireEvent.change(typeSelect, {
      target: { value: "pi" },
    });

    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "https://generativelanguage.googleapis.com" },
    });

    fireEvent.change(screen.getByLabelText("Model"), {
      target: { value: "gemini-pro" },
    });

    // Mock the POST and subsequent reload
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 201,
          text: () => Promise.resolve(JSON.stringify(createdProvider)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([createdProvider])),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
        }),
      );

    // Submit
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => {
      expect(screen.queryByText("New Provider")).toBeFalsy();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/aiproviders"),
      expect.objectContaining({ method: "POST" }),
    );
  });

  test("selecting the copilot CLI-auth type hides connection fields and shows the login note", async () => {
    const fetchMock = mockFetch(null);
    // Initial load order: providers, supported types (incl. copilot), agents.
    fetchMock
      .mockReturnValueOnce(jsonResponse([]))
      .mockReturnValueOnce(jsonResponse(["opencode", "pi", "claude-code", "copilot"]))
      .mockReturnValueOnce(jsonResponse([]));

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("+ New Provider"));
    await waitFor(() => {
      expect(screen.getByText("New Provider")).toBeTruthy();
    });

    // Base URL / Model / API Key are shown until a CLI-auth type is picked.
    expect(screen.getByLabelText("Base URL")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Type"), {
      target: { value: "copilot" },
    });

    // CLI-auth: connection fields disappear, the login note takes their place.
    expect(screen.queryByLabelText("Base URL")).toBeFalsy();
    expect(screen.queryByLabelText("Model")).toBeFalsy();
    expect(screen.queryByLabelText("API Key")).toBeFalsy();
    expect(screen.getByText("/login")).toBeTruthy();
    // The note is Copilot-specific rather than the Claude Code wording.
    const note = screen.getByText(
      (_, element) =>
        element?.className === "ap-cli-note" &&
        (element.textContent ?? "").includes("GitHub Copilot"),
    );
    expect(note).toBeTruthy();
  });

  test("edit form pre-fills fields and calls update API on save", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "Pi",
        type: "pi",
        baseUrl: "http://pi.local",
        apiKey: "old-key",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const updatedProvider = {
      ...providers[0],
      name: "Pi Updated",
    };

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(providers)),
      }),
    );
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    // Open edit form
    fireEvent.click(screen.getByText("Edit"));
    await waitFor(() => {
      expect(screen.getByText("Edit Provider")).toBeTruthy();
    });

    // Form should be pre-filled
    const nameInput = screen.getByLabelText("Name");
    expect((nameInput as HTMLInputElement).value).toBe("Pi");

    // Change name
    fireEvent.change(nameInput, {
      target: { value: "Pi Updated" },
    });

    // Mock the PUT and subsequent reload
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(updatedProvider)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([updatedProvider])),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
        }),
      );

    // Submit
    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => {
      expect(screen.queryByText("Edit Provider")).toBeFalsy();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/aiproviders/ai-1"),
      expect.objectContaining({ method: "PUT" }),
    );
  });

  test("Set as default button only shows on non-default providers and calls API", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "Pi",
        type: "pi",
        baseUrl: "http://pi.local",
        apiKey: "key",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "ai-2",
        name: "OpenCode",
        type: "opencode",
        baseUrl: "http://opencode.local",
        apiKey: "key",
        model: "claude-3",
        isDefault: false,
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(providers)),
      }),
    );
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    // Exactly one "Set as default" button (only on the non-default OpenCode card).
    const buttons = screen.getAllByText("Set as default");
    expect(buttons.length).toBe(1);

    const promoted = { ...providers[1], isDefault: true };
    const demoted = { ...providers[0], isDefault: false };

    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(promoted)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([demoted, promoted])),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
        }),
      );

    fireEvent.click(buttons[0]);

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/aiproviders/ai-2/set-default"),
        expect.objectContaining({ method: "POST" }),
      );
    });
  });

  test("shows installed + latest version and an enabled Update button when behind", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: "0.80.1",
        latestVersion: "0.80.2",
        updateAvailable: true,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    await openAgentsPopover();

    expect(screen.getByText("0.80.1 → 0.80.2")).toBeTruthy();
    const updateBtn = screen.getByText("Update → 0.80.2") as HTMLButtonElement;
    expect(updateBtn.disabled).toBe(false);
  });

  test("shows an 'Up to date' indicator (no action) when the agent is current", async () => {
    const agents = [
      {
        key: "opencode",
        displayName: "OpenCode",
        npmPackage: "opencode-ai",
        installedVersion: "1.17.9",
        latestVersion: "1.17.9",
        updateAvailable: false,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    await openAgentsPopover();

    // A current agent shows a plain status indicator, not an actionable button.
    const upToDate = screen.getByText("Up to date");
    expect(upToDate.tagName).toBe("SPAN");
    expect(screen.queryByRole("button", { name: /Update|Install/ })).toBeNull();
  });

  test("a not-installed agent raises no badge but still offers its install", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: null,
        latestVersion: "0.80.2",
        updateAvailable: true,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    const bell = await screen.findByRole("button", { name: /Coding agents/i });
    expect(bell.getAttribute("aria-label")).toBe("Coding agents — all up to date");
    expect(bell.querySelector(".ap-bell-dot")).toBeNull();

    fireEvent.click(bell);

    expect(screen.getByText("All up to date")).toBeTruthy();
    const installBtn = screen.getByText("Install 0.80.2") as HTMLButtonElement;
    expect(installBtn.disabled).toBe(false);
  });

  test("the badge counts installed agents that are behind, not ones awaiting install", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: "0.80.1",
        latestVersion: "0.80.2",
        updateAvailable: true,
        error: null,
      },
      {
        key: "opencode",
        displayName: "OpenCode",
        npmPackage: "opencode-ai",
        installedVersion: null,
        latestVersion: "1.17.9",
        updateAvailable: true,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    const bell = await screen.findByRole("button", { name: /Coding agents/i });
    expect(bell.getAttribute("aria-label")).toBe("Coding agents — 1 update available");
    expect(bell.querySelector(".ap-bell-dot")?.textContent).toBe("1");

    fireEvent.click(bell);

    expect(screen.getByText("1 update available")).toBeTruthy();
    expect(screen.getByText("Update → 0.80.2")).toBeTruthy();
    expect(screen.getByText("Install 1.17.9")).toBeTruthy();
  });

  test("offers an enabled Install button when the agent is not installed", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: null,
        latestVersion: "0.80.2",
        updateAvailable: true,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    await openAgentsPopover();

    expect(screen.getByText("not installed → 0.80.2")).toBeTruthy();
    const installBtn = screen.getByText("Install 0.80.2") as HTMLButtonElement;
    expect(installBtn.disabled).toBe(false);
  });

  test("clicking Install installs the agent and reflects the new version", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: null,
        latestVersion: "0.80.2",
        updateAvailable: true,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    await openAgentsPopover();

    fetchMock.mockReturnValueOnce(
      jsonResponse({
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: "0.80.2",
        latestVersion: "0.80.2",
        updateAvailable: false,
        error: null,
      }),
    );

    fireEvent.click(screen.getByText("Install 0.80.2"));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/managedagents/pi/update"),
        expect.objectContaining({ method: "POST" }),
      );
    });

    await waitFor(() => {
      expect(screen.getByText("Up to date")).toBeTruthy();
    });
  });

  test("Install button is disabled when not installed and latest is unknown", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: null,
        latestVersion: null,
        updateAvailable: false,
        error: "Could not reach the npm registry.",
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    await openAgentsPopover();

    const btn = screen.getByText("Unavailable") as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
  });

  test("clicking Update calls the update API and reflects the new version", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: "0.80.1",
        latestVersion: "0.80.2",
        updateAvailable: true,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    await openAgentsPopover();

    fetchMock.mockReturnValueOnce(
      jsonResponse({
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: "0.80.2",
        latestVersion: "0.80.2",
        updateAvailable: false,
        error: null,
      }),
    );

    fireEvent.click(screen.getByText("Update → 0.80.2"));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/managedagents/pi/update"),
        expect.objectContaining({ method: "POST" }),
      );
    });

    await waitFor(() => {
      expect(screen.getByText("Up to date")).toBeTruthy();
    });
  });

  test("shows an error when the update fails and leaves the button actionable", async () => {
    const agents = [
      {
        key: "pi",
        displayName: "Pi",
        npmPackage: "@earendil-works/pi-coding-agent",
        installedVersion: "0.80.1",
        latestVersion: "0.80.2",
        updateAvailable: true,
        error: null,
      },
    ];

    const fetchMock = mockFetch(null);
    queueInitialLoad(fetchMock, [], agents);
    renderPage(fetchMock);

    await openAgentsPopover();

    fetchMock.mockReturnValueOnce(jsonResponse({ error: "npm install failed" }, 502));

    fireEvent.click(screen.getByText("Update → 0.80.2"));

    await waitFor(() => {
      expect(screen.getByText("npm install failed")).toBeTruthy();
    });

    // The previous version is intact; the button is still offering the same update.
    expect(screen.getByText("Update → 0.80.2")).toBeTruthy();
  });

  test("shows default badge for default provider", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "Pi",
        type: "pi",
        baseUrl: "http://pi.local",
        apiKey: "key",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "ai-2",
        name: "OpenCode",
        type: "opencode",
        baseUrl: "http://opencode.local",
        apiKey: "key",
        model: "claude-3",
        isDefault: false,
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(providers)),
      }),
    );
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(["opencode", "pi"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    expect(screen.getByText("Default")).toBeTruthy();
  });

  test("edit modal renders the Custom MCP servers field for an opencode provider and seeds its value", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "OpenCode",
        type: "opencode",
        baseUrl: "http://opencode.local",
        apiKey: "key",
        model: "claude-3",
        isDefault: false,
        customMcpServersJson: '{"chrome-devtools":{"command":["npx"]}}',
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const requests: Array<{ url: string; method: string; body: unknown }> = [];
    renderRouted(
      routingFetch({ providers, types: ["opencode", "pi"], schema: customMcpSchema, requests }),
    );

    await waitFor(() => expect(screen.getByText("AI Providers")).toBeTruthy());

    fireEvent.click(screen.getByText("Edit"));

    // The schema-driven field appears and is pre-filled from the provider's
    // non-secret customMcpServersJson value.
    const textarea = (await screen.findByLabelText(
      "Custom MCP servers (JSON)",
    )) as HTMLTextAreaElement;
    expect(textarea.tagName).toBe("TEXTAREA");
    expect(textarea.value).toBe('{"chrome-devtools":{"command":["npx"]}}');
  });

  test("edit modal shows no Custom MCP servers field for a pi provider", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "Pi",
        type: "pi",
        baseUrl: "http://pi.local",
        apiKey: "key",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const requests: Array<{ url: string; method: string; body: unknown }> = [];
    // The pi adapter advertises an empty schema, so no config fields render.
    renderRouted(routingFetch({ providers, types: ["opencode", "pi"], schema: [], requests }));

    await waitFor(() => expect(screen.getByText("AI Providers")).toBeTruthy());

    fireEvent.click(screen.getByText("Edit"));
    await waitFor(() => expect(screen.getByText("Edit Provider")).toBeTruthy());

    // Let any config-schema fetch settle, then confirm the field is absent.
    await waitFor(() => {
      const schemaFetched = requests.some((r) => r.url.includes("config-schema"));
      expect(schemaFetched).toBe(true);
    });
    expect(screen.queryByLabelText("Custom MCP servers (JSON)")).toBeFalsy();
  });

  test("renders only the Custom MCP servers field, filtering out schema fields the save path can't persist", async () => {
    // The save path only round-trips customMcpServersJson, so any other schema
    // field the backend might advertise must not render (it would silently no-op).
    const schemaWithExtra = [
      {
        name: "temperature",
        type: ConfigFieldType.Number,
        label: "Temperature",
        required: false,
        defaultValue: 0.7,
        description: "Not persisted by this form.",
        options: null,
      },
      ...customMcpSchema,
    ];

    const providers = [
      {
        id: "ai-1",
        name: "OpenCode",
        type: "opencode",
        baseUrl: "http://opencode.local",
        apiKey: "key",
        model: "claude-3",
        isDefault: false,
        customMcpServersJson: '{"a":{"command":["npx"]}}',
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const requests: Array<{ url: string; method: string; body: unknown }> = [];
    renderRouted(
      routingFetch({ providers, types: ["opencode", "pi"], schema: schemaWithExtra, requests }),
    );

    await waitFor(() => expect(screen.getByText("AI Providers")).toBeTruthy());

    fireEvent.click(screen.getByText("Edit"));

    // The supported field renders…
    await screen.findByLabelText("Custom MCP servers (JSON)");
    // …but the unsupported extra field is filtered out.
    expect(screen.queryByLabelText("Temperature")).toBeFalsy();
  });

  test("round-trips the Custom MCP servers value into the save payload", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "OpenCode",
        type: "opencode",
        baseUrl: "http://opencode.local",
        apiKey: "key",
        model: "claude-3",
        isDefault: false,
        customMcpServersJson: '{"old":{"command":["npx"]}}',
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const requests: Array<{ url: string; method: string; body: unknown }> = [];
    renderRouted(
      routingFetch({
        providers,
        types: ["opencode", "pi"],
        schema: customMcpSchema,
        onWrite: () => providers[0],
        requests,
      }),
    );

    await waitFor(() => expect(screen.getByText("AI Providers")).toBeTruthy());

    fireEvent.click(screen.getByText("Edit"));

    const textarea = await screen.findByLabelText("Custom MCP servers (JSON)");
    const newValue = '{"chrome-devtools":{"command":["npx","-y","chrome-devtools-mcp@latest"]}}';
    fireEvent.change(textarea, { target: { value: newValue } });

    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => expect(screen.queryByText("Edit Provider")).toBeFalsy());

    const putReq = requests.find((r) => r.method === "PUT" && r.url.includes("/aiproviders/ai-1"));
    expect(putReq).toBeTruthy();
    // The edited value rides on the dedicated, non-secret field; the server folds
    // it into AiProvider.Config, preserving any other stored keys.
    expect((putReq!.body as { customMcpServersJson?: string }).customMcpServersJson).toBe(newValue);
  });
});
