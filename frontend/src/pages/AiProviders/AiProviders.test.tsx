import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import AiProviders from "./index";

afterEach(() => {
  cleanup();
});

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

    await waitFor(() => {
      expect(screen.getByText("Coding agents")).toBeTruthy();
    });

    expect(screen.getByText("0.80.1")).toBeTruthy();
    expect(screen.getByText("0.80.2")).toBeTruthy();
    const updateBtn = screen.getByText("Update 0.80.1 → 0.80.2") as HTMLButtonElement;
    expect(updateBtn.disabled).toBe(false);
  });

  test("Update button is disabled when the agent is up to date", async () => {
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

    await waitFor(() => {
      expect(screen.getByText("Coding agents")).toBeTruthy();
    });

    const upToDate = screen.getByText("Up to date") as HTMLButtonElement;
    expect(upToDate.disabled).toBe(true);
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

    await waitFor(() => {
      expect(screen.getByText("Coding agents")).toBeTruthy();
    });

    expect(screen.getByText("not installed")).toBeTruthy();
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

    await waitFor(() => {
      expect(screen.getByText("Coding agents")).toBeTruthy();
    });

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

    await waitFor(() => {
      expect(screen.getByText("Coding agents")).toBeTruthy();
    });

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

    await waitFor(() => {
      expect(screen.getByText("Coding agents")).toBeTruthy();
    });

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

    fireEvent.click(screen.getByText("Update 0.80.1 → 0.80.2"));

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

    await waitFor(() => {
      expect(screen.getByText("Coding agents")).toBeTruthy();
    });

    fetchMock.mockReturnValueOnce(jsonResponse({ error: "npm install failed" }, 502));

    fireEvent.click(screen.getByText("Update 0.80.1 → 0.80.2"));

    await waitFor(() => {
      expect(screen.getByText("npm install failed")).toBeTruthy();
    });

    // The previous version is intact; the button is still offering the same update.
    expect(screen.getByText("Update 0.80.1 → 0.80.2")).toBeTruthy();
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
});
