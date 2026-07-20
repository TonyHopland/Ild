import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import Repositories from "./index";

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
        <Repositories />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

describe("Repositories page", () => {
  test("renders repository list with name, clone URL, provider, and gating setting", async () => {
    const repos = [
      {
        id: "repo-1",
        name: "my-repo",
        cloneUrl: "https://git.example.com/my-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "main",
        worktreesPath: null,
        defaultIntakeStatus: "Backlog",
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "repo-2",
        name: "other-repo",
        cloneUrl: "https://git.example.com/other-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "develop",
        worktreesPath: "/worktrees",
        defaultIntakeStatus: "WorkQueue",
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("my-repo")).toBeTruthy();
    });

    expect(screen.getByText("my-repo")).toBeTruthy();
    expect(screen.getByText("other-repo")).toBeTruthy();
    expect(screen.getByText("https://git.example.com/my-repo.git")).toBeTruthy();
    expect(screen.getByText("Backlog")).toBeTruthy();
    expect(screen.getByText("WorkQueue")).toBeTruthy();
  });

  test("create form opens, validates required fields, and calls API on submit", async () => {
    const repos: unknown[] = [];
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const createdRepo = {
      id: "new-repo-1",
      name: "new-repo",
      cloneUrl: "https://git.example.com/new-repo.git",
      remoteProviderId: "prov-1",
      defaultBranch: "main",
      worktreesPath: null,
      defaultIntakeStatus: "Backlog",
      createdAt: "2025-03-01T00:00:00Z",
    };

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Repositories")).toBeTruthy();
    });

    // Open create form
    fireEvent.click(screen.getByText("+ New Repository"));
    await waitFor(() => {
      expect(screen.getByText("New Repository")).toBeTruthy();
    });

    // Fill in required fields
    fireEvent.change(screen.getByLabelText("Name"), {
      target: { value: "new-repo" },
    });
    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/new-repo.git" },
    });
    fireEvent.change(screen.getByLabelText("Remote Provider"), {
      target: { value: "prov-1" },
    });

    // Mock the POST and subsequent reload
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 201,
          text: () => Promise.resolve(JSON.stringify(createdRepo)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([createdRepo])),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    // Submit
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => {
      expect(screen.queryByText("New Repository")).toBeFalsy();
    });

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/repositories"),
      expect.objectContaining({ method: "POST" }),
    );
  });

  test("create form renders the Custom .env field and submits its value", async () => {
    const repos: unknown[] = [];
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const createdRepo = {
      id: "new-repo-1",
      name: "new-repo",
      cloneUrl: "https://git.example.com/new-repo.git",
      remoteProviderId: "prov-1",
      defaultBranch: "main",
      worktreesPath: null,
      defaultIntakeStatus: "Backlog",
      hasPreviewEnv: true,
      createdAt: "2025-03-01T00:00:00Z",
    };

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Repositories")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("+ New Repository"));
    await waitFor(() => {
      expect(screen.getByText("New Repository")).toBeTruthy();
    });

    // The field is present…
    const envField = screen.getByLabelText("Custom .env") as HTMLTextAreaElement;
    expect(envField).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Name"), {
      target: { value: "new-repo" },
    });
    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/new-repo.git" },
    });
    fireEvent.change(screen.getByLabelText("Remote Provider"), {
      target: { value: "prov-1" },
    });
    fireEvent.change(envField, {
      target: { value: "API_TOKEN=secret\nFOO=bar" },
    });

    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 201,
          text: () => Promise.resolve(JSON.stringify(createdRepo)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([createdRepo])),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => {
      expect(screen.queryByText("New Repository")).toBeFalsy();
    });

    // …and its value rides along in the POST body.
    const postCall = fetchMock.mock.calls.find(
      (c: unknown[]) =>
        typeof c[0] === "string" &&
        (c[0] as string).includes("/repositories") &&
        (c[1] as { method?: string })?.method === "POST",
    );
    expect(postCall).toBeTruthy();
    const body = JSON.parse((postCall![1] as { body: string }).body);
    expect(body.previewEnv).toBe("API_TOKEN=secret\nFOO=bar");
  });

  test("edit form leaves the Custom .env blank and omits it when unchanged", async () => {
    const repos = [
      {
        id: "repo-1",
        name: "my-repo",
        cloneUrl: "https://git.example.com/my-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "main",
        worktreesPath: null,
        defaultIntakeStatus: "Backlog",
        hasPreviewEnv: true,
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("my-repo")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Edit"));
    await waitFor(() => {
      expect(screen.getByText("Edit Repository")).toBeTruthy();
    });

    // Never pre-filled with the secret — starts blank even when one is stored.
    const envField = screen.getByLabelText("Custom .env") as HTMLTextAreaElement;
    expect(envField.value).toBe("");

    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos[0])),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => {
      expect(screen.queryByText("Edit Repository")).toBeFalsy();
    });

    const putCall = fetchMock.mock.calls.find(
      (c: unknown[]) => (c[1] as { method?: string })?.method === "PUT",
    );
    expect(putCall).toBeTruthy();
    const body = JSON.parse((putCall![1] as { body: string }).body);
    // A blank field must not send previewEnv, so the stored secret is kept.
    expect("previewEnv" in body).toBe(false);
  });

  test("auto-fills name and default branch from the remote on clone-URL blur", async () => {
    const repos: unknown[] = [];
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Repositories")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("+ New Repository"));
    await waitFor(() => {
      expect(screen.getByText("New Repository")).toBeTruthy();
    });

    // The remote inspection responds with the advertised default branch + name.
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () =>
          Promise.resolve(JSON.stringify({ name: "inspect-proj", defaultBranch: "develop" })),
      }),
    );

    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/inspect-proj.git" },
    });
    fireEvent.blur(screen.getByLabelText("Clone URL"));

    await waitFor(() => {
      expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("inspect-proj");
    });
    expect((screen.getByLabelText("Default Branch") as HTMLInputElement).value).toBe("develop");

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/repositories/inspect-remote"),
      expect.objectContaining({ method: "POST" }),
    );
  });

  test("clone-URL blur degrades gracefully when the remote can't be inspected", async () => {
    const repos: unknown[] = [];
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Repositories")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("+ New Repository"));
    await waitFor(() => {
      expect(screen.getByText("New Repository")).toBeTruthy();
    });

    // Unfetchable remote: server returns empty fields; the form keeps its default.
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify({ name: null, defaultBranch: null })),
      }),
    );

    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/private.git" },
    });
    fireEvent.blur(screen.getByLabelText("Clone URL"));

    await waitFor(() => {
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining("/repositories/inspect-remote"),
        expect.objectContaining({ method: "POST" }),
      );
    });

    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("Default Branch") as HTMLInputElement).value).toBe("main");
  });

  test("delete shows confirmation and removes repository on confirm", async () => {
    const repos = [
      {
        id: "repo-1",
        name: "my-repo",
        cloneUrl: "https://git.example.com/my-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "main",
        worktreesPath: null,
        defaultIntakeStatus: "Backlog",
        createdAt: "2025-01-01T00:00:00Z",
      },
      {
        id: "repo-2",
        name: "other-repo",
        cloneUrl: "https://git.example.com/other-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "develop",
        worktreesPath: "/worktrees",
        defaultIntakeStatus: "WorkQueue",
        createdAt: "2025-02-01T00:00:00Z",
      },
    ];

    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("my-repo")).toBeTruthy();
    });

    // Click delete on first repo
    const deleteButtons = screen.getAllByText("Delete");
    fireEvent.click(deleteButtons[0]);

    // Confirm dialog appears
    await waitFor(() => {
      expect(screen.getByText("Confirm")).toBeTruthy();
    });

    // Mock the DELETE and subsequent reload
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 204,
          text: () => Promise.resolve(""),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify([repos[1]])),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    // Confirm delete
    fireEvent.click(screen.getByText("Confirm"));

    await waitFor(() => {
      expect(screen.queryByText("my-repo")).toBeFalsy();
    });

    expect(screen.queryByText("my-repo")).toBeFalsy();
    expect(screen.getByText("other-repo")).toBeTruthy();

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/repositories/repo-1"),
      expect.objectContaining({ method: "DELETE" }),
    );
  });

  test("delete does not open the repository form dialog", async () => {
    const repos = [
      {
        id: "repo-1",
        name: "my-repo",
        cloneUrl: "https://git.example.com/my-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "main",
        worktreesPath: null,
        defaultIntakeStatus: "Backlog",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("my-repo")).toBeTruthy();
    });

    // Clicking delete should reveal the inline confirmation only — not the
    // create/edit form modal, which would otherwise overlay and block the
    // Confirm button.
    fireEvent.click(screen.getByText("Delete"));

    await waitFor(() => {
      expect(screen.getByText("Confirm")).toBeTruthy();
    });

    expect(screen.queryByText("New Repository")).toBeFalsy();
    expect(screen.queryByText("Edit Repository")).toBeFalsy();
  });

  test("edit button opens modal with repository data pre-filled", async () => {
    const repos = [
      {
        id: "repo-1",
        name: "my-repo",
        cloneUrl: "https://git.example.com/my-repo.git",
        remoteProviderId: "prov-1",
        defaultBranch: "main",
        worktreesPath: null,
        defaultIntakeStatus: "Backlog",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "gitea",
        baseUrl: "https://git.example.com",
        apiKey: "",
        webhookSecret: "",
        createdAt: "2025-01-01T00:00:00Z",
      },
    ];

    const fetchMock = mockFetch(null);
    fetchMock
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(repos)),
        }),
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(providers)),
        }),
      );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("my-repo")).toBeTruthy();
    });

    // Click edit button
    fireEvent.click(screen.getByText("Edit"));

    // Modal should open with "Edit Repository" title
    await waitFor(() => {
      expect(screen.getByText("Edit Repository")).toBeTruthy();
    });

    // Form fields should be pre-filled with repository data
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("my-repo");
    expect((screen.getByLabelText("Clone URL") as HTMLInputElement).value).toBe(
      "https://git.example.com/my-repo.git",
    );
    expect((screen.getByLabelText("Default Branch") as HTMLInputElement).value).toBe("main");
  });
});
