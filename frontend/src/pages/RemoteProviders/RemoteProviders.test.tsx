import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import RemoteProviders from "./index";

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
        <RemoteProviders />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

describe("Remote Providers page", () => {
  test("renders provider list with name, type, and URL", async () => {
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "Forgejo",
        baseUrl: "https://git.example.com",
        apiKey: "secret-key-1",
        webhookSecret: "wh-secret-1",
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "prov-2",
        name: "GitHub",
        type: "GitHub",
        baseUrl: "https://github.com",
        apiKey: "secret-key-2",
        webhookSecret: "wh-secret-2",
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

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Remote Providers")).toBeTruthy();
    });

    expect(screen.getByText("https://git.example.com")).toBeTruthy();
    expect(screen.getByText("https://github.com")).toBeTruthy();
  });

  test("API key is masked in the list view", async () => {
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "Forgejo",
        baseUrl: "https://git.example.com",
        apiKey: "super-secret-api-key",
        webhookSecret: "wh-secret",
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

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Remote Providers")).toBeTruthy();
    });

    expect(screen.queryByText("super-secret-api-key")).toBeFalsy();
    expect(screen.getByText("••••••••")).toBeTruthy();
  });

  test("create form opens, validates required fields, and calls API on submit", async () => {
    const providers: unknown[] = [];

    const createdProvider = {
      id: "new-prov-1",
      name: "GitLab",
      type: "GitLab",
      baseUrl: "https://gitlab.com",
      apiKey: "new-key",
      webhookSecret: "",
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

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Remote Providers")).toBeTruthy();
    });

    // Open create form
    fireEvent.click(screen.getByText("+ New Provider"));
    await waitFor(() => {
      expect(screen.getByText("New Provider")).toBeTruthy();
    });

    // Fill in required fields
    fireEvent.change(screen.getByLabelText("Name"), {
      target: { value: "GitLab" },
    });

    const typeSelect = screen.getByLabelText("Type");
    fireEvent.change(typeSelect, {
      target: { value: "GitLab" },
    });

    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "https://gitlab.com" },
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
      );

    // Submit
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => {
      expect(screen.queryByText("New Provider")).toBeFalsy();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/remoteproviders"),
      expect.objectContaining({ method: "POST" }),
    );
  });

  test("edit form pre-fills fields and calls update API on save", async () => {
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "Forgejo",
        baseUrl: "https://git.example.com",
        apiKey: "old-key",
        webhookSecret: "wh-secret",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const updatedProvider = {
      ...providers[0],
      name: "Forgejo Updated",
    };

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(providers)),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Remote Providers")).toBeTruthy();
    });

    // Open edit form
    fireEvent.click(screen.getByText("Edit"));
    await waitFor(() => {
      expect(screen.getByText("Edit Provider")).toBeTruthy();
    });

    // Form should be pre-filled
    const nameInput = screen.getByLabelText("Name");
    expect((nameInput as HTMLInputElement).value).toBe("Forgejo");

    // Change name
    fireEvent.change(nameInput, {
      target: { value: "Forgejo Updated" },
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
      );

    // Submit
    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => {
      expect(screen.queryByText("Edit Provider")).toBeFalsy();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/remoteproviders/prov-1"),
      expect.objectContaining({ method: "PUT" }),
    );
  });
});
