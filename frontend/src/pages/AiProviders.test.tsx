import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../hooks/useAuth";
import AiProviders from "./AiProviders";

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
        name: "OpenAI",
        type: "OpenAI",
        baseUrl: "https://api.openai.com",
        apiKey: "sk-secret",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "ai-2",
        name: "Anthropic",
        type: "Anthropic",
        baseUrl: "https://api.anthropic.com",
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
        text: () => Promise.resolve(JSON.stringify(["openai", "opencode"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    expect(screen.getByText("gpt-4")).toBeTruthy();
    expect(screen.getByText("claude-3")).toBeTruthy();
    expect(screen.getByText("https://api.openai.com")).toBeTruthy();
  });

  test("API key is masked in the list view", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "OpenAI",
        type: "OpenAI",
        baseUrl: "https://api.openai.com",
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
        text: () => Promise.resolve(JSON.stringify(["openai", "opencode"])),
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
      name: "Gemini",
      type: "Google",
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
        text: () => Promise.resolve(JSON.stringify(["openai", "opencode"])),
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
      target: { value: "Gemini" },
    });

    const typeSelect = screen.getByLabelText("Type");
    fireEvent.change(typeSelect, {
      target: { value: "openai" },
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
          text: () => Promise.resolve(JSON.stringify(["openai", "opencode"])),
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
        name: "OpenAI",
        type: "OpenAI",
        baseUrl: "https://api.openai.com",
        apiKey: "old-key",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const updatedProvider = {
      ...providers[0],
      name: "OpenAI Updated",
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
        text: () => Promise.resolve(JSON.stringify(["openai", "opencode"])),
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
    expect((nameInput as HTMLInputElement).value).toBe("OpenAI");

    // Change name
    fireEvent.change(nameInput, {
      target: { value: "OpenAI Updated" },
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
          text: () => Promise.resolve(JSON.stringify(["openai", "opencode"])),
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

  test("shows default badge for default provider", async () => {
    const providers = [
      {
        id: "ai-1",
        name: "OpenAI",
        type: "OpenAI",
        baseUrl: "https://api.openai.com",
        apiKey: "key",
        model: "gpt-4",
        isDefault: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "ai-2",
        name: "Anthropic",
        type: "Anthropic",
        baseUrl: "https://api.anthropic.com",
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
        text: () => Promise.resolve(JSON.stringify(["openai", "opencode"])),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("AI Providers")).toBeTruthy();
    });

    expect(screen.getByText("Default")).toBeTruthy();
  });
});
