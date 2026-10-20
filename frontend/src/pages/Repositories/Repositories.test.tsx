import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import Repositories from "./index";
import * as services from "../../services/auth";

// The feed list is its own request, made on mount; stubbing it keeps the queued
// fetch responses below in step with the requests they answer.
beforeEach(() => {
  vi.spyOn(services.packageFeedService, "list").mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
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
    fireEvent.click(screen.getByText("+ Add repository"));
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

    fireEvent.click(screen.getByText("+ Add repository"));
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

    // A second New Repository form starts blank — the previous repository's .env
    // must not be sitting in it, silently unsent because it looks unchanged.
    fireEvent.click(screen.getByText("+ Add repository"));
    await waitFor(() => {
      expect(screen.getByText("New Repository")).toBeTruthy();
    });
    expect((screen.getByLabelText("Custom .env") as HTMLTextAreaElement).value).toBe("");
  });

  test("edit form prefills the stored Custom .env and omits it when unchanged", async () => {
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

    // The list load must not have reached for the plaintext .env.
    expect(
      fetchMock.mock.calls.some(
        (c: unknown[]) => typeof c[0] === "string" && (c[0] as string).includes("/preview-env"),
      ),
    ).toBe(false);

    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify({ previewEnv: "API_TOKEN=stored" })),
      }),
    );

    fireEvent.click(within(repoRow("my-repo")).getByText("Edit"));
    await waitFor(() => {
      expect(screen.getByText("Edit Repository")).toBeTruthy();
    });

    // Opening the editor fetches the stored text and prefills it, so an edit is an
    // edit rather than a retype.
    const envField = screen.getByLabelText("Custom .env") as HTMLTextAreaElement;
    await waitFor(() => {
      expect(envField.value).toBe("API_TOKEN=stored");
    });

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
    // An untouched field must not send previewEnv, so the stored secret is kept.
    expect("previewEnv" in body).toBe(false);
  });

  test("emptying the prefilled Custom .env removes the stored value", async () => {
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

    const ok = (json: unknown) =>
      Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve(JSON.stringify(json)) });

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(ok(repos)).mockReturnValueOnce(ok(providers));

    renderPage(fetchMock);
    await waitFor(() => {
      expect(screen.getByText("my-repo")).toBeTruthy();
    });

    fetchMock.mockReturnValueOnce(ok({ previewEnv: "API_TOKEN=stored" }));
    fireEvent.click(within(repoRow("my-repo")).getByText("Edit"));

    const envField = screen.getByLabelText("Custom .env") as HTMLTextAreaElement;
    await waitFor(() => {
      expect(envField.value).toBe("API_TOKEN=stored");
    });

    fireEvent.change(envField, { target: { value: "" } });

    fetchMock
      .mockReturnValueOnce(ok(repos[0]))
      .mockReturnValueOnce(ok({ ...repos[0], hasPreviewEnv: false }))
      .mockReturnValueOnce(ok(repos))
      .mockReturnValueOnce(ok(providers));

    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => {
      expect(screen.queryByText("Edit Repository")).toBeFalsy();
    });

    // The PUT reads a blank .env as "keep it", so removing takes the explicit
    // clear call instead.
    const putCall = fetchMock.mock.calls.find(
      (c: unknown[]) => (c[1] as { method?: string })?.method === "PUT",
    );
    expect("previewEnv" in JSON.parse((putCall![1] as { body: string }).body)).toBe(false);
    expect(
      fetchMock.mock.calls.some(
        (c: unknown[]) =>
          typeof c[0] === "string" &&
          (c[0] as string).endsWith("/repositories/repo-1/preview-env") &&
          (c[1] as { method?: string })?.method === "DELETE",
      ),
    ).toBe(true);
  });

  test("a Custom .env that cannot be read says so instead of looking unset", async () => {
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

    const ok = (json: unknown) =>
      Promise.resolve({ ok: true, status: 200, text: () => Promise.resolve(JSON.stringify(json)) });

    const fetchMock = mockFetch(null);
    fetchMock.mockReturnValueOnce(ok(repos)).mockReturnValueOnce(ok(providers));

    renderPage(fetchMock);
    await waitFor(() => {
      expect(screen.getByText("my-repo")).toBeTruthy();
    });

    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: false,
        status: 500,
        statusText: "Server Error",
        text: () => Promise.resolve(""),
      }),
    );
    fireEvent.click(within(repoRow("my-repo")).getByText("Edit"));

    // An empty field would otherwise be indistinguishable from "no .env stored".
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/Couldn't load the stored custom \.env/);

    fetchMock
      .mockReturnValueOnce(ok(repos[0]))
      .mockReturnValueOnce(ok(repos))
      .mockReturnValueOnce(ok(providers));

    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => {
      expect(screen.queryByText("Edit Repository")).toBeFalsy();
    });

    // The blank field is not read as a removal: nothing about the .env is sent.
    const putCall = fetchMock.mock.calls.find(
      (c: unknown[]) => (c[1] as { method?: string })?.method === "PUT",
    );
    expect("previewEnv" in JSON.parse((putCall![1] as { body: string }).body)).toBe(false);
    expect(
      fetchMock.mock.calls.some(
        (c: unknown[]) => (c[1] as { method?: string })?.method === "DELETE",
      ),
    ).toBe(false);
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

    fireEvent.click(screen.getByText("+ Add repository"));
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

    fireEvent.click(screen.getByText("+ Add repository"));
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
    fireEvent.click(within(repoRow("my-repo")).getByText("Edit"));

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

type FakeResponse = { ok: boolean; status: number; text: () => Promise<string> };

function reply(body: unknown, status = 200): FakeResponse {
  return { ok: status < 400, status, text: () => Promise.resolve(JSON.stringify(body)) };
}

const testProviders = [
  {
    id: "prov-1",
    name: "Forgejo",
    type: "Forgejo",
    baseUrl: "https://git.example.com",
    apiKey: "***",
    webhookSecret: null,
    createdAt: "2025-01-01T00:00:00Z",
  },
];

function repo(id: string, name: string) {
  return {
    id,
    name,
    cloneUrl: `https://git.example.com/${name}.git`,
    remoteProviderId: "prov-1",
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: "Backlog",
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: null,
  };
}

/** Answers the page's list/update/.env calls from `state`; test calls go to `onTest`. */
function routedFetch(
  state: { repos: Record<string, unknown>[] },
  onTest: (id: string) => Promise<FakeResponse>,
) {
  return vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const test = /\/repositories\/([^/]+)\/test$/.exec(url);
    if (test && method === "POST") return onTest(test[1]);
    if (/\/repositories\/[^/]+\/preview-env$/.test(url))
      return Promise.resolve(reply({ previewEnv: null }));
    const one = /\/repositories\/([^/?]+)$/.exec(url);
    if (one && method === "PUT") {
      state.repos = state.repos.map((r) =>
        r.id === one[1] ? { ...r, updatedAt: new Date(Date.now() + 60_000).toISOString() } : r,
      );
      return Promise.resolve(reply(state.repos.find((r) => r.id === one[1])));
    }
    if (url.includes("/remoteproviders")) return Promise.resolve(reply(testProviders));
    if (url.includes("/repositories")) return Promise.resolve(reply(state.repos));
    return Promise.resolve(reply(null, 404));
  });
}

function repoRow(name: string): HTMLElement {
  return screen.getByText(name).closest(".repo-row") as HTMLElement;
}

describe("Repositories page — Test button", () => {
  test("each card tests its own repository and shows the result in that card only", async () => {
    const state = { repos: [repo("repo-1", "alpha-app"), repo("repo-2", "beta-app")] };
    const fetchMock = routedFetch(state, () =>
      Promise.resolve(
        reply({
          ok: false,
          outcome: "BranchMissing",
          message: "The default branch 'main' is not on the remote.",
          detail: "ls-remote exited 2",
        }),
      ),
    );
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("alpha-app")).toBeTruthy());

    expect(within(repoRow("alpha-app")).getByRole("button", { name: "Test" })).toBeTruthy();
    fireEvent.click(within(repoRow("beta-app")).getByRole("button", { name: "Test" }));

    await within(repoRow("beta-app")).findByText("The default branch 'main' is not on the remote.");
    expect(
      within(repoRow("beta-app")).getByText("ls-remote exited 2").closest("pre"),
    ).not.toBeNull();
    expect(
      within(repoRow("alpha-app")).queryByText("The default branch 'main' is not on the remote."),
    ).toBeNull();
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/repositories/repo-2/test"),
      expect.objectContaining({ method: "POST" }),
    );
  });

  test("saving a repository clears its result", async () => {
    const state = { repos: [repo("repo-1", "alpha-app")] };
    renderPage(
      routedFetch(state, () =>
        Promise.resolve(
          reply({ ok: true, outcome: "Ok", message: "Reached branch main.", detail: null }),
        ),
      ),
    );
    await waitFor(() => expect(screen.getByText("alpha-app")).toBeTruthy());

    fireEvent.click(within(repoRow("alpha-app")).getByRole("button", { name: "Test" }));
    await within(repoRow("alpha-app")).findByText("Reached branch main.");

    fireEvent.click(within(repoRow("alpha-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Repository")).toBeNull());

    await waitFor(() => expect(screen.queryByText("Reached branch main.")).toBeNull());
  });
});

/** The page's list/update/create/.env calls, plus the preview-data reset, answered from `state`. */
function previewDataFetch(
  state: { repos: Record<string, unknown>[] },
  onReset: (id: string) => Promise<FakeResponse> = () => Promise.resolve(reply({})),
) {
  return vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const reset = /\/storage\/preview-data\/([^/]+)$/.exec(url);
    if (reset && method === "DELETE") return onReset(reset[1]);
    if (/\/repositories\/[^/]+\/preview-env$/.test(url))
      return Promise.resolve(reply({ previewEnv: null }));
    const one = /\/repositories\/([^/?]+)$/.exec(url);
    if (one && method === "PUT") {
      const sent = JSON.parse(String(init?.body));
      state.repos = state.repos.map((r) => (r.id === one[1] ? { ...r, ...sent, id: r.id } : r));
      return Promise.resolve(reply(state.repos.find((r) => r.id === one[1])));
    }
    if (url.endsWith("/repositories") && method === "POST") {
      const created = { ...repo("repo-new", "new-repo"), ...JSON.parse(String(init?.body)) };
      state.repos = [...state.repos, created];
      return Promise.resolve(reply(created, 201));
    }
    if (url.includes("/remoteproviders")) return Promise.resolve(reply(testProviders));
    if (url.includes("/repositories")) return Promise.resolve(reply(state.repos));
    return Promise.resolve(reply(null, 404));
  });
}

const keepCheckbox = () =>
  screen.getByLabelText("Keep preview data between runs") as HTMLInputElement;

function sentBodies(fetchMock: ReturnType<typeof vi.fn>, method: string) {
  return fetchMock.mock.calls
    .filter((c: unknown[]) => (c[1] as { method?: string } | undefined)?.method === method)
    .map((c: unknown[]) => ({
      url: c[0] as string,
      body: (c[1] as { body?: string }).body ? JSON.parse((c[1] as { body: string }).body) : null,
    }));
}

function resetRequests(fetchMock: ReturnType<typeof vi.fn>) {
  return sentBodies(fetchMock, "DELETE").filter((c) => c.url.includes("/storage/preview-data/"));
}

describe("Repositories page — preview data", () => {
  test("the edit form shows the stored Keep preview data setting and saves a change to it", async () => {
    const state = {
      repos: [
        { ...repo("repo-1", "alpha-app"), keepPreviewData: true },
        { ...repo("repo-2", "beta-app"), keepPreviewData: false },
      ],
    };
    const fetchMock = previewDataFetch(state);
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("alpha-app")).toBeTruthy());

    fireEvent.click(within(repoRow("alpha-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    expect(keepCheckbox().checked).toBe(true);

    fireEvent.click(keepCheckbox());
    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Repository")).toBeNull());

    const put = sentBodies(fetchMock, "PUT").find((c) => c.url.endsWith("/repositories/repo-1"));
    expect(put?.body.keepPreviewData).toBe(false);

    fireEvent.click(within(repoRow("beta-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    expect(keepCheckbox().checked).toBe(false);
  });

  test("the create form starts with Keep preview data off, offers no reset, and sends the setting", async () => {
    const fetchMock = previewDataFetch({ repos: [] });
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("+ Add repository")).toBeTruthy());

    fireEvent.click(screen.getByText("+ Add repository"));
    await screen.findByText("New Repository");
    expect(keepCheckbox().checked).toBe(false);
    expect(screen.queryByRole("button", { name: "Reset preview data" })).toBeNull();

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "new-repo" } });
    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/new-repo.git" },
    });
    fireEvent.change(screen.getByLabelText("Remote Provider"), { target: { value: "prov-1" } });
    fireEvent.click(keepCheckbox());
    fireEvent.click(screen.getByText("Create"));
    await waitFor(() => expect(screen.queryByText("New Repository")).toBeNull());

    const post = sentBodies(fetchMock, "POST").find((c) => c.url.endsWith("/repositories"));
    expect(post?.body.keepPreviewData).toBe(true);
  });

  test("Reset preview data says it wipes every work item's preview data and resets only once confirmed", async () => {
    const state = { repos: [{ ...repo("repo-1", "alpha-app"), keepPreviewData: true }] };
    const fetchMock = previewDataFetch(state);
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("alpha-app")).toBeTruthy());

    fireEvent.click(within(repoRow("alpha-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    fireEvent.click(screen.getByRole("button", { name: "Reset preview data" }));

    expect(
      screen.getByText(/wipes the preview data for every work item in alpha-app/i),
    ).toBeTruthy();
    expect(resetRequests(fetchMock)).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: /^confirm/i }));

    await waitFor(() => expect(resetRequests(fetchMock)).toHaveLength(1));
    expect(resetRequests(fetchMock)[0].url).toMatch(/\/storage\/preview-data\/repo-1$/);
  });

  test("a refused reset shows the reason in the form, and not in another repository's", async () => {
    const refusal =
      "A preview is already running for work item 305 (Title). Stop it before resetting the preview data.";
    const state = {
      repos: [
        { ...repo("repo-1", "alpha-app"), keepPreviewData: true },
        { ...repo("repo-2", "beta-app"), keepPreviewData: true },
      ],
    };
    const fetchMock = previewDataFetch(state, () =>
      Promise.resolve(reply({ error: refusal }, 409)),
    );
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("alpha-app")).toBeTruthy());

    fireEvent.click(within(repoRow("alpha-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    fireEvent.click(screen.getByRole("button", { name: "Reset preview data" }));
    fireEvent.click(screen.getByRole("button", { name: /^confirm/i }));

    await waitFor(() =>
      expect(screen.getAllByRole("alert").some((a) => a.textContent?.includes(refusal))).toBe(true),
    );

    fireEvent.click(screen.getByText("×"));
    await waitFor(() => expect(screen.queryByText("Edit Repository")).toBeNull());
    fireEvent.click(within(repoRow("beta-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    expect(screen.queryByText(refusal)).toBeNull();
  });
});
