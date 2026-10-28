import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import Repositories from "./index";
import * as services from "../../services/auth";
import { reply, stubFetch, type Call } from "../../test-support";

// The feed list is its own request, made on mount; stubbing it keeps the queued
// fetch responses below in step with the requests they answer.
beforeEach(() => {
  vi.spyOn(services.packageFeedService, "list").mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

function renderPage(mockFetchFn: ReturnType<typeof vi.fn>) {
  vi.stubGlobal("fetch", mockFetchFn);
  renderPageOnly();
}

function renderPageOnly() {
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
  const provider = {
    id: "prov-1",
    name: "Forgejo",
    type: "gitea",
    baseUrl: "https://git.example.com",
    apiKey: "",
    webhookSecret: "",
    createdAt: "2025-01-01T00:00:00Z",
  };

  const storedRepo = (id: string, name: string, extra: Record<string, unknown> = {}) => ({
    ...repo(id, name),
    ...extra,
  });

  interface Server {
    repos: Record<string, unknown>[];
    previewEnv: Response | string;
    inspection: { name: string | null; defaultBranch: string | null };
  }

  /** A server holding `server.repos`, applying each create, update and delete to them. */
  function serve(init: Partial<Server> = {}) {
    const server: Server = {
      repos: [],
      previewEnv: "",
      inspection: { name: null, defaultBranch: null },
      ...init,
    };
    const calls = stubFetch((call) => {
      const { method, path, body } = call;
      const one = /\/repositories\/([^/]+)$/.exec(path);
      const env = /\/repositories\/([^/]+)\/preview-env$/.exec(path);
      if (path.endsWith("/remoteproviders")) return reply(200, [provider]);
      if (path.endsWith("/repositories/inspect-remote")) return reply(200, server.inspection);
      if (env && method === "GET")
        return typeof server.previewEnv === "string"
          ? reply(200, { previewEnv: server.previewEnv })
          : server.previewEnv;
      if (env && method === "DELETE")
        return reply(200, { ...server.repos.find((r) => r.id === env[1]), hasPreviewEnv: false });
      if (one && method === "PUT")
        return reply(
          200,
          server.repos.find((r) => r.id === one[1]),
        );
      if (one && method === "DELETE") {
        server.repos = server.repos.filter((r) => r.id !== one[1]);
        return reply(204);
      }
      if (path.endsWith("/repositories") && method === "POST") {
        const created = { ...repo("repo-new", String(body?.name)), ...body };
        server.repos = [...server.repos, created];
        return reply(201, created);
      }
      if (path.endsWith("/repositories")) return reply(200, server.repos);
      return reply(404);
    });
    return { server, calls };
  }

  const requestsOf = (calls: Call[], method: string, pathEnd: string) =>
    calls.filter((c) => c.method === method && c.path.endsWith(pathEnd));

  async function openEditorOf(name: string) {
    await waitFor(() => expect(screen.getByText(name)).toBeTruthy());
    fireEvent.click(within(repoRow(name)).getByText("Edit"));
    await screen.findByText("Edit Repository");
  }

  async function openCreateForm() {
    await waitFor(() => expect(screen.getByText("Repositories")).toBeTruthy());
    fireEvent.click(screen.getByText("+ Add repository"));
    await screen.findByText("New Repository");
  }

  function fillIdentity(name: string, cloneUrl: string) {
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: name } });
    fireEvent.change(screen.getByLabelText("Clone URL"), { target: { value: cloneUrl } });
    fireEvent.change(screen.getByLabelText("Remote Provider"), { target: { value: "prov-1" } });
  }

  test("renders repository list with name, clone URL, provider, and gating setting", async () => {
    serve({
      repos: [
        storedRepo("repo-1", "my-repo", { defaultIntakeStatus: "Backlog" }),
        storedRepo("repo-2", "other-repo", { defaultIntakeStatus: "WorkQueue" }),
      ],
    });

    renderPageOnly();

    await waitFor(() => expect(screen.getByText("my-repo")).toBeTruthy());
    expect(screen.getByText("other-repo")).toBeTruthy();
    expect(screen.getByText("https://git.example.com/my-repo.git")).toBeTruthy();
    expect(screen.getByText("Backlog")).toBeTruthy();
    expect(screen.getByText("WorkQueue")).toBeTruthy();
  });

  test("create form renders the Custom .env field and submits its value", async () => {
    const { calls } = serve();
    renderPageOnly();
    await openCreateForm();

    const envField = screen.getByLabelText("Custom .env") as HTMLTextAreaElement;
    fillIdentity("new-repo", "https://git.example.com/new-repo.git");
    fireEvent.change(envField, { target: { value: "API_TOKEN=secret\nFOO=bar" } });
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => expect(screen.queryByText("New Repository")).toBeFalsy());
    const posts = requestsOf(calls, "POST", "/repositories");
    expect(posts).toHaveLength(1);
    expect(posts[0].body).toMatchObject({
      name: "new-repo",
      cloneUrl: "https://git.example.com/new-repo.git",
      remoteProviderId: "prov-1",
      previewEnv: "API_TOKEN=secret\nFOO=bar",
    });

    // A second New Repository form starts blank: the previous repository's .env
    // must not be sitting in it, silently unsent because it looks unchanged.
    fireEvent.click(screen.getByText("+ Add repository"));
    await screen.findByText("New Repository");
    expect((screen.getByLabelText("Custom .env") as HTMLTextAreaElement).value).toBe("");
  });

  test("edit form prefills the stored Custom .env and omits it when unchanged", async () => {
    const { calls } = serve({
      repos: [storedRepo("repo-1", "my-repo", { hasPreviewEnv: true })],
      previewEnv: "API_TOKEN=stored",
    });
    renderPageOnly();
    await waitFor(() => expect(screen.getByText("my-repo")).toBeTruthy());

    // The list load must not have reached for the plaintext .env.
    expect(requestsOf(calls, "GET", "/preview-env")).toHaveLength(0);

    fireEvent.click(within(repoRow("my-repo")).getByText("Edit"));
    await screen.findByText("Edit Repository");
    const envField = screen.getByLabelText("Custom .env") as HTMLTextAreaElement;
    await waitFor(() => expect(envField.value).toBe("API_TOKEN=stored"));

    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Repository")).toBeFalsy());

    // An untouched field must not send previewEnv, so the stored secret is kept.
    const puts = requestsOf(calls, "PUT", "/repositories/repo-1");
    expect(puts).toHaveLength(1);
    expect(puts[0].body).toMatchObject({ name: "my-repo", remoteProviderId: "prov-1" });
    expect(puts[0].body).not.toHaveProperty("previewEnv");
  });

  test("emptying the prefilled Custom .env removes the stored value", async () => {
    const { calls } = serve({
      repos: [storedRepo("repo-1", "my-repo", { hasPreviewEnv: true })],
      previewEnv: "API_TOKEN=stored",
    });
    renderPageOnly();
    await openEditorOf("my-repo");

    const envField = screen.getByLabelText("Custom .env") as HTMLTextAreaElement;
    await waitFor(() => expect(envField.value).toBe("API_TOKEN=stored"));
    fireEvent.change(envField, { target: { value: "" } });
    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Repository")).toBeFalsy());

    // The PUT reads a blank .env as "keep it", so removing takes the explicit
    // clear call instead.
    const puts = requestsOf(calls, "PUT", "/repositories/repo-1");
    expect(puts).toHaveLength(1);
    expect(puts[0].body).not.toHaveProperty("previewEnv");
    expect(requestsOf(calls, "DELETE", "/repositories/repo-1/preview-env")).toHaveLength(1);
  });

  test("a Custom .env that cannot be read says so instead of looking unset", async () => {
    const { calls } = serve({
      repos: [storedRepo("repo-1", "my-repo", { hasPreviewEnv: true })],
      previewEnv: reply(500),
    });
    renderPageOnly();
    await waitFor(() => expect(screen.getByText("my-repo")).toBeTruthy());

    fireEvent.click(within(repoRow("my-repo")).getByText("Edit"));

    // An empty field would otherwise be indistinguishable from "no .env stored".
    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/Couldn't load the stored custom \.env/);

    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Repository")).toBeFalsy());

    // The blank field is not read as a removal: nothing about the .env is sent.
    const puts = requestsOf(calls, "PUT", "/repositories/repo-1");
    expect(puts).toHaveLength(1);
    expect(puts[0].body).not.toHaveProperty("previewEnv");
    expect(calls.filter((c) => c.method === "DELETE")).toHaveLength(0);
  });

  test("auto-fills name and default branch from the remote on clone-URL blur", async () => {
    const { calls } = serve({ inspection: { name: "inspect-proj", defaultBranch: "develop" } });
    renderPageOnly();
    await openCreateForm();

    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/inspect-proj.git" },
    });
    fireEvent.blur(screen.getByLabelText("Clone URL"));

    await waitFor(() =>
      expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("inspect-proj"),
    );
    expect((screen.getByLabelText("Default Branch") as HTMLInputElement).value).toBe("develop");
    const inspections = requestsOf(calls, "POST", "/repositories/inspect-remote");
    expect(inspections).toHaveLength(1);
    expect(inspections[0].body).toMatchObject({
      cloneUrl: "https://git.example.com/inspect-proj.git",
    });
  });

  test("clone-URL blur degrades gracefully when the remote can't be inspected", async () => {
    const { calls } = serve({ inspection: { name: null, defaultBranch: null } });
    renderPageOnly();
    await openCreateForm();

    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/private.git" },
    });
    fireEvent.blur(screen.getByLabelText("Clone URL"));

    await waitFor(() =>
      expect(requestsOf(calls, "POST", "/repositories/inspect-remote")).toHaveLength(1),
    );
    expect(requestsOf(calls, "POST", "/repositories/inspect-remote")[0].body).toMatchObject({
      cloneUrl: "https://git.example.com/private.git",
    });
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("");
    expect((screen.getByLabelText("Default Branch") as HTMLInputElement).value).toBe("main");
  });

  test("delete shows confirmation and removes repository on confirm", async () => {
    const { calls } = serve({
      repos: [storedRepo("repo-1", "my-repo"), storedRepo("repo-2", "other-repo")],
    });
    renderPageOnly();
    await waitFor(() => expect(screen.getByText("my-repo")).toBeTruthy());

    fireEvent.click(screen.getAllByText("Delete")[0]);
    await screen.findByText("Confirm");
    expect(calls.filter((c) => c.method === "DELETE")).toHaveLength(0);

    fireEvent.click(screen.getByText("Confirm"));

    await waitFor(() => expect(screen.queryByText("my-repo")).toBeFalsy());
    expect(screen.getByText("other-repo")).toBeTruthy();
    expect(calls.filter((c) => c.method === "DELETE").map((c) => c.path)).toEqual([
      "/api/v1/repositories/repo-1",
    ]);
  });

  test("delete does not open the repository form dialog", async () => {
    serve({ repos: [storedRepo("repo-1", "my-repo")] });
    renderPageOnly();
    await waitFor(() => expect(screen.getByText("my-repo")).toBeTruthy());

    // Clicking delete should reveal the inline confirmation only — not the
    // create/edit form modal, which would otherwise overlay and block the
    // Confirm button.
    fireEvent.click(screen.getByText("Delete"));

    await screen.findByText("Confirm");
    expect(screen.queryByText("New Repository")).toBeFalsy();
    expect(screen.queryByText("Edit Repository")).toBeFalsy();
  });

  test("edit button opens modal with repository data pre-filled", async () => {
    serve({ repos: [storedRepo("repo-1", "my-repo")] });
    renderPageOnly();

    await openEditorOf("my-repo");

    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("my-repo");
    expect((screen.getByLabelText("Clone URL") as HTMLInputElement).value).toBe(
      "https://git.example.com/my-repo.git",
    );
    expect((screen.getByLabelText("Default Branch") as HTMLInputElement).value).toBe("main");
  });
});

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
  onTest: (id: string) => Promise<Response>,
) {
  return vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const test = /\/repositories\/([^/]+)\/test$/.exec(url);
    if (test && method === "POST") return onTest(test[1]);
    if (/\/repositories\/[^/]+\/preview-env$/.test(url))
      return Promise.resolve(reply(200, { previewEnv: null }));
    const one = /\/repositories\/([^/?]+)$/.exec(url);
    if (one && method === "PUT") {
      state.repos = state.repos.map((r) =>
        r.id === one[1] ? { ...r, updatedAt: new Date(Date.now() + 60_000).toISOString() } : r,
      );
      const updated = state.repos.find((r) => r.id === one[1]);
      return Promise.resolve(reply(200, updated));
    }
    if (url.includes("/remoteproviders")) return Promise.resolve(reply(200, testProviders));
    if (url.includes("/repositories")) return Promise.resolve(reply(200, state.repos));
    return Promise.resolve(reply(404));
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
        reply(200, {
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
          reply(200, { ok: true, outcome: "Ok", message: "Reached branch main.", detail: null }),
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
  onReset: (id: string) => Promise<Response> = () => Promise.resolve(reply(200, {})),
) {
  return vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const reset = /\/storage\/preview-data\/([^/]+)$/.exec(url);
    if (reset && method === "DELETE") return onReset(reset[1]);
    if (/\/repositories\/[^/]+\/preview-env$/.test(url))
      return Promise.resolve(reply(200, { previewEnv: null }));
    const one = /\/repositories\/([^/?]+)$/.exec(url);
    if (one && method === "PUT") {
      const sent = JSON.parse(init?.body as string);
      state.repos = state.repos.map((r) => (r.id === one[1] ? { ...r, ...sent, id: r.id } : r));
      const updated = state.repos.find((r) => r.id === one[1]);
      return Promise.resolve(reply(200, updated));
    }
    if (url.endsWith("/repositories") && method === "POST") {
      const created = { ...repo("repo-new", "new-repo"), ...JSON.parse(init?.body as string) };
      state.repos = [...state.repos, created];
      return Promise.resolve(reply(201, created));
    }
    if (url.includes("/remoteproviders")) return Promise.resolve(reply(200, testProviders));
    if (url.includes("/repositories")) return Promise.resolve(reply(200, state.repos));
    return Promise.resolve(reply(404));
  });
}

const keepCheckbox = () =>
  screen.getByLabelText("Keep preview data between runs") as HTMLInputElement;

const parallelismInput = () =>
  screen.getByLabelText("Parallelism (0 = unlimited)") as HTMLInputElement;

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
      Promise.resolve(reply(409, { error: refusal })),
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

describe("Repositories page — parallelism", () => {
  test("the edit form shows each repository's stored parallelism and saves a change to it", async () => {
    const state = {
      repos: [
        { ...repo("repo-1", "alpha-app"), parallelism: 2 },
        { ...repo("repo-2", "beta-app"), parallelism: 0 },
      ],
    };
    const fetchMock = previewDataFetch(state);
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("alpha-app")).toBeTruthy());

    fireEvent.click(within(repoRow("alpha-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    expect(parallelismInput().value).toBe("2");

    fireEvent.change(parallelismInput(), { target: { value: "1" } });
    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Repository")).toBeNull());

    const put = sentBodies(fetchMock, "PUT").find((c) => c.url.endsWith("/repositories/repo-1"));
    expect(put?.body.parallelism).toBe(1);

    fireEvent.click(within(repoRow("beta-app")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Repository");
    expect(parallelismInput().value).toBe("0");
  });

  test("the create form starts at 0 (unlimited) and sends the entered parallelism", async () => {
    const fetchMock = previewDataFetch({ repos: [] });
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("+ Add repository")).toBeTruthy());

    fireEvent.click(screen.getByText("+ Add repository"));
    await screen.findByText("New Repository");
    expect(parallelismInput().value).toBe("0");

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "new-repo" } });
    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/new-repo.git" },
    });
    fireEvent.change(screen.getByLabelText("Remote Provider"), { target: { value: "prov-1" } });
    fireEvent.change(parallelismInput(), { target: { value: "3" } });
    fireEvent.click(screen.getByText("Create"));
    await waitFor(() => expect(screen.queryByText("New Repository")).toBeNull());

    const post = sentBodies(fetchMock, "POST").find((c) => c.url.endsWith("/repositories"));
    expect(post?.body.parallelism).toBe(3);
  });
});
