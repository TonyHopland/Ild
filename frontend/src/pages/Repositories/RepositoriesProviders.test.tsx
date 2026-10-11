import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import Repositories from "./index";
import * as services from "../../services/auth";

const availableTypes = [{ type: "Forgejo" }, { type: "GitHub" }];

beforeEach(() => {
  vi.spyOn(services.packageFeedService, "list").mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

type FakeResponse = { ok: boolean; status: number; text: () => Promise<string> };

function reply(body: unknown, status = 200): FakeResponse {
  return { ok: status < 400, status, text: () => Promise.resolve(JSON.stringify(body)) };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

type Row = Record<string, unknown>;

function routedFetch(
  state: { providers: Row[]; repos: Row[] },
  onTest: (id: string) => Promise<FakeResponse> = () => Promise.resolve(reply(null, 404)),
  onRepoTest: (id: string) => Promise<FakeResponse> = () => Promise.resolve(reply(null, 404)),
) {
  return vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const later = () => new Date(Date.now() + 60_000).toISOString();

    const providerTest = /\/remoteproviders\/([^/]+)\/test$/.exec(url);
    if (providerTest && method === "POST") return onTest(providerTest[1]);
    const repoTest = /\/repositories\/([^/]+)\/test$/.exec(url);
    if (repoTest && method === "POST") return onRepoTest(repoTest[1]);

    if (url.includes("/remoteproviders/types")) return Promise.resolve(reply(availableTypes));
    const oneProvider = /\/remoteproviders\/([^/?]+)$/.exec(url);
    if (oneProvider && method === "PUT") {
      const body = JSON.parse((init?.body as string | undefined) ?? "{}");
      state.providers = state.providers.map((p) =>
        p.id === oneProvider[1] ? { ...p, ...body, updatedAt: later() } : p,
      );
      return Promise.resolve(reply(state.providers.find((p) => p.id === oneProvider[1])));
    }
    if (url.includes("/remoteproviders") && method === "POST") {
      const body = JSON.parse((init?.body as string | undefined) ?? "{}");
      const created = { ...body, id: `prov-${state.providers.length + 1}`, createdAt: later() };
      state.providers = [...state.providers, created];
      return Promise.resolve(reply(created, 201));
    }
    if (url.includes("/remoteproviders")) return Promise.resolve(reply(state.providers));

    if (/\/repositories\/[^/]+\/preview-env$/.test(url))
      return Promise.resolve(reply({ previewEnv: null }));
    const oneRepo = /\/repositories\/([^/?]+)$/.exec(url);
    if (oneRepo && method === "PUT") {
      state.repos = state.repos.map((r) =>
        r.id === oneRepo[1] ? { ...r, updatedAt: later() } : r,
      );
      return Promise.resolve(reply(state.repos.find((r) => r.id === oneRepo[1])));
    }
    if (url.includes("/repositories") && method === "GET")
      return Promise.resolve(reply(state.repos));
    return Promise.resolve(reply(null, 404));
  });
}

function renderPage(fetchMock: ReturnType<typeof routedFetch>) {
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
        <Repositories />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

function provider(id: string, name: string, extra: Row = {}): Row {
  return {
    id,
    name,
    type: "Forgejo",
    baseUrl: `https://${id}.example.com`,
    apiKey: "***",
    hasApiKey: true,
    webhookSecret: null,
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: null,
    ...extra,
  };
}

function repo(id: string, name: string, remoteProviderId: string): Row {
  return {
    id,
    name,
    cloneUrl: `https://git.example.com/${name}.git`,
    remoteProviderId,
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: "Backlog",
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: null,
  };
}

function card(name: string): HTMLElement {
  return screen.getByRole("region", { name });
}

function repoRow(name: string): HTMLElement {
  return screen.getByText(name).closest(".repo-row") as HTMLElement;
}

describe("Repositories page — providers", () => {
  test("each provider is a card holding its own repositories", async () => {
    renderPage(
      routedFetch({
        providers: [
          provider("prov-1", "Alpha Forge"),
          provider("prov-2", "Beta Forge", { type: "GitHub" }),
        ],
        repos: [repo("repo-1", "alpha-app", "prov-1"), repo("repo-2", "beta-app", "prov-2")],
      }),
    );
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    expect(within(card("Alpha Forge")).getByText("https://prov-1.example.com")).toBeTruthy();
    expect(within(card("Alpha Forge")).getByText("Forgejo")).toBeTruthy();
    expect(within(card("Alpha Forge")).getByText("Repositories (1)")).toBeTruthy();
    expect(within(card("Alpha Forge")).getByText("alpha-app")).toBeTruthy();
    expect(within(card("Alpha Forge")).queryByText("beta-app")).toBeNull();
    expect(within(card("Beta Forge")).getByText("GitHub")).toBeTruthy();
    expect(within(card("Beta Forge")).getByText("beta-app")).toBeTruthy();
  });

  test("the API key is masked", async () => {
    renderPage(
      routedFetch({
        providers: [provider("prov-1", "Alpha Forge", { apiKey: "super-secret-api-key" })],
        repos: [],
      }),
    );
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    expect(screen.queryByText("super-secret-api-key")).toBeNull();
    expect(screen.getByText("••••••••")).toBeTruthy();
  });

  test("a provider without repositories says so", async () => {
    renderPage(routedFetch({ providers: [provider("prov-1", "Alpha Forge")], repos: [] }));
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    expect(within(card("Alpha Forge")).getByText("Repositories (0)")).toBeTruthy();
    expect(within(card("Alpha Forge")).getByText("No repositories yet.")).toBeTruthy();
  });

  test("with no providers the page asks for one first", async () => {
    renderPage(routedFetch({ providers: [], repos: [] }));

    await screen.findByText("Add a remote provider first — repositories are added under one.");
    expect(screen.getAllByRole("button", { name: "+ New provider" })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "+ Add repository" })).toBeNull();
  });

  test("a repository whose provider is not in the list still shows", async () => {
    renderPage(
      routedFetch({
        providers: [provider("prov-1", "Alpha Forge")],
        repos: [repo("repo-1", "lost-app", "prov-gone")],
      }),
    );
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    expect(within(card("Provider not found")).getByText("lost-app")).toBeTruthy();
    expect(within(card("Alpha Forge")).queryByText("lost-app")).toBeNull();
  });

  test("a repository whose provider is past the first page sits under that provider", async () => {
    const providers = Array.from({ length: 501 }, (_, i) => provider(`prov-${i}`, `Forge ${i}`));
    const fetchMock = routedFetch({ providers, repos: [repo("repo-1", "late-app", "prov-500")] });
    const route = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation((url: string, init?: RequestInit) => {
      const list = /\/remoteproviders(?:\?skip=(\d+)&take=(\d+))?$/.exec(url);
      if (!list || (init?.method ?? "GET") !== "GET") return route(url, init);
      // The server answers 100 rows when no take is given.
      const skip = Number(list[1] ?? 0);
      return Promise.resolve(reply(providers.slice(skip, skip + Number(list[2] ?? 100))));
    });
    renderPage(fetchMock);

    await screen.findByRole("region", { name: "Forge 500" });
    expect(within(card("Forge 500")).getByText("late-app")).toBeTruthy();
    expect(screen.queryByRole("region", { name: "Provider not found" })).toBeNull();
  });

  test("Add repository opens the repository form with that provider chosen", async () => {
    renderPage(
      routedFetch({
        providers: [provider("prov-1", "Alpha Forge"), provider("prov-2", "Beta Forge")],
        repos: [],
      }),
    );
    await waitFor(() => expect(screen.getByText("Beta Forge")).toBeTruthy());

    fireEvent.click(within(card("Beta Forge")).getByRole("button", { name: "+ Add repository" }));
    await screen.findByText("New Repository");

    expect((screen.getByLabelText("Remote Provider") as HTMLSelectElement).value).toBe("prov-2");
  });

  test("the create form offers the server's types and posts a webhook secret", async () => {
    const state = { providers: [] as Row[], repos: [] as Row[] };
    const fetchMock = routedFetch(state);
    renderPage(fetchMock);

    fireEvent.click(await screen.findByRole("button", { name: "+ New provider" }));
    await screen.findByText("New Provider");
    await screen.findByRole("option", { name: "GitHub" });

    expect(screen.queryByRole("option", { name: "GitLab" })).toBeNull();
    expect((screen.getByLabelText("Webhook Secret") as HTMLInputElement).value).not.toBe("");

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "GitHub" } });
    fireEvent.change(screen.getByLabelText("Type"), { target: { value: "GitHub" } });
    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "https://github.com" },
    });
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => expect(screen.queryByText("New Provider")).toBeNull());
    const postCall = fetchMock.mock.calls.find(
      ([url, options]) => String(url).endsWith("/remoteproviders") && options?.method === "POST",
    );
    expect(postCall).toBeTruthy();
    expect(JSON.parse(String(postCall?.[1]?.body ?? "{}")).webhookSecret).toBeTruthy();

    await waitFor(() => expect(card("GitHub")).toBeTruthy());
    expect(
      screen.queryByText("Add a remote provider first — repositories are added under one."),
    ).toBeNull();
  });

  test("the edit form pre-fills the provider and saves it", async () => {
    const fetchMock = routedFetch({
      providers: [provider("prov-1", "Forgejo", { webhookSecret: "***" })],
      repos: [],
    });
    renderPage(fetchMock);
    await screen.findByRole("region", { name: "Forgejo" });

    fireEvent.click(within(card("Forgejo")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Provider");

    const nameInput = screen.getByLabelText("Name") as HTMLInputElement;
    expect(nameInput.value).toBe("Forgejo");
    expect((screen.getByLabelText("API Key") as HTMLInputElement).placeholder).toBe("(unchanged)");
    expect((screen.getByLabelText("Webhook Secret") as HTMLInputElement).placeholder).toBe(
      "(unchanged)",
    );

    fireEvent.change(nameInput, { target: { value: "Forgejo Updated" } });
    fireEvent.click(screen.getByText("Update"));

    await waitFor(() => expect(screen.queryByText("Edit Provider")).toBeNull());
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/remoteproviders/prov-1"),
      expect.objectContaining({ method: "PUT" }),
    );
    await waitFor(() => expect(card("Forgejo Updated")).toBeTruthy());
  });

  test("the webhook secret can be regenerated in the form", async () => {
    renderPage(routedFetch({ providers: [], repos: [] }));

    fireEvent.click(await screen.findByRole("button", { name: "+ New provider" }));

    const webhookSecretInput = screen.getByLabelText("Webhook Secret") as HTMLInputElement;
    const initialValue = webhookSecretInput.value;
    fireEvent.click(screen.getByText("Generate"));

    expect(webhookSecretInput.value).not.toBe("");
    expect(webhookSecretInput.value).not.toBe(initialValue);
  });
});

describe("Repositories page — provider form", () => {
  test("it is a named modal dialog with a labelled close button", async () => {
    renderPage(routedFetch({ providers: [], repos: [] }));
    fireEvent.click(await screen.findByRole("button", { name: "+ New provider" }));

    const dialog = await screen.findByRole("dialog", { name: "New Provider" });
    expect(dialog.getAttribute("aria-modal")).toBe("true");
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });

  test("an empty required field stops the save before any request", async () => {
    const fetchMock = routedFetch({ providers: [], repos: [] });
    renderPage(fetchMock);
    fireEvent.click(await screen.findByRole("button", { name: "+ New provider" }));
    await screen.findByRole("option", { name: "GitHub" });

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "GitHub" } });
    fireEvent.change(screen.getByLabelText("Type"), { target: { value: "GitHub" } });
    fireEvent.click(screen.getByText("Create"));
    await new Promise((r) => setTimeout(r, 0));

    expect(
      fetchMock.mock.calls.some(
        ([, options]) => (options as RequestInit | undefined)?.method === "POST",
      ),
    ).toBe(false);
    expect(screen.getByRole("dialog", { name: "New Provider" })).toBeTruthy();
  });

  test("a refused save says why and keeps the form open", async () => {
    const fetchMock = routedFetch({ providers: [], repos: [] });
    const route = fetchMock.getMockImplementation()!;
    fetchMock.mockImplementation((url: string, init?: RequestInit) =>
      String(url).endsWith("/remoteproviders") && init?.method === "POST"
        ? Promise.resolve(reply({ error: "Unsupported remote provider type." }, 400))
        : route(url, init),
    );
    renderPage(fetchMock);
    fireEvent.click(await screen.findByRole("button", { name: "+ New provider" }));
    await screen.findByRole("option", { name: "GitHub" });

    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "GitHub" } });
    fireEvent.change(screen.getByLabelText("Type"), { target: { value: "GitHub" } });
    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "https://github.example" },
    });
    fireEvent.click(screen.getByText("Create"));

    await screen.findByText("Couldn't save the provider: Unsupported remote provider type.");
    expect(screen.getByRole("dialog", { name: "New Provider" })).toBeTruthy();
  });

  test("saving a provider keeps it the default", async () => {
    const fetchMock = routedFetch({
      providers: [provider("prov-1", "Alpha Forge", { isDefault: true })],
      repos: [],
    });
    renderPage(fetchMock);
    await screen.findByRole("region", { name: "Alpha Forge" });

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Edit" }));
    await screen.findByText("Edit Provider");
    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Provider")).toBeNull());

    const putCall = fetchMock.mock.calls.find(
      ([, options]) => (options as RequestInit | undefined)?.method === "PUT",
    );
    const putBody = (putCall?.[1] as RequestInit | undefined)?.body as string;
    expect(JSON.parse(putBody).isDefault).toBe(true);
  });

  test("a stored type in other casing is the catalog's type, listed once", async () => {
    renderPage(
      routedFetch({
        providers: [provider("prov-1", "Alpha Forge", { type: "github" })],
        repos: [],
      }),
    );
    await screen.findByRole("region", { name: "Alpha Forge" });

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Edit" }));
    await screen.findByRole("option", { name: "GitHub" });

    expect(screen.queryByRole("option", { name: "github" })).toBeNull();
    expect((screen.getByLabelText("Type") as HTMLSelectElement).value).toBe("GitHub");
  });

  test("with only repositories whose provider is missing, a provider can still be added", async () => {
    renderPage(routedFetch({ providers: [], repos: [repo("repo-1", "lost-app", "prov-gone")] }));

    await screen.findByRole("region", { name: "Provider not found" });
    fireEvent.click(screen.getByRole("button", { name: "+ New provider" }));
    expect(await screen.findByRole("dialog", { name: "New Provider" })).toBeTruthy();
  });
});

describe("Repositories page — provider Test button", () => {
  test("each card tests its own provider and shows the result in that card only", async () => {
    const state = {
      providers: [provider("prov-1", "Alpha Forge"), provider("prov-2", "Beta Forge")],
      repos: [],
    };
    const pending: Record<string, ReturnType<typeof deferred<FakeResponse>>> = {};
    const fetchMock = routedFetch(state, (id) => {
      pending[id] = deferred<FakeResponse>();
      return pending[id].promise;
    });
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Test" }));

    const running = await within(card("Alpha Forge")).findByRole("button", { name: "Testing…" });
    expect((running as HTMLButtonElement).disabled).toBe(true);
    expect(
      (within(card("Beta Forge")).getByRole("button", { name: "Test" }) as HTMLButtonElement)
        .disabled,
    ).toBe(false);
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining("/remoteproviders/prov-1/test"),
      expect.objectContaining({ method: "POST" }),
    );

    pending["prov-1"].resolve(
      reply({ ok: true, outcome: "Ok", message: "Signed in as forge-bot.", detail: null }),
    );
    await within(card("Alpha Forge")).findByText("Signed in as forge-bot.");
    expect(card("Alpha Forge").querySelector("pre")).toBeNull();
    expect(within(card("Beta Forge")).queryByText("Signed in as forge-bot.")).toBeNull();

    fireEvent.click(within(card("Beta Forge")).getByRole("button", { name: "Test" }));
    pending["prov-2"].resolve(
      reply({
        ok: false,
        outcome: "InvalidApiKey",
        message: "The API key was rejected.",
        detail: "HTTP 401: user does not exist",
      }),
    );

    await within(card("Beta Forge")).findByText("The API key was rejected.");
    const detail = within(card("Beta Forge")).getByText("HTTP 401: user does not exist");
    expect(detail.closest("pre")).not.toBeNull();
    expect(within(card("Alpha Forge")).getByText("Signed in as forge-bot.")).toBeTruthy();
  });

  test("a call to ILD that fails says the test couldn't run, with the error", async () => {
    const answers: Array<() => Promise<FakeResponse>> = [
      () => Promise.reject(new TypeError("Failed to fetch")),
      () => Promise.resolve(reply({ error: "boom" }, 500)),
    ];
    renderPage(
      routedFetch({ providers: [provider("prov-1", "Alpha Forge")], repos: [] }, () =>
        answers.shift()!(),
      ),
    );
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Test" }));
    await within(card("Alpha Forge")).findByText("Couldn't run the test: Failed to fetch");

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Test" }));
    await within(card("Alpha Forge")).findByText("Couldn't run the test: boom");
    expect(within(card("Alpha Forge")).queryByText(/Failed to fetch/)).toBeNull();
  });

  test("saving a provider clears its result, and drops one still in flight", async () => {
    const pending: Array<ReturnType<typeof deferred<FakeResponse>>> = [];
    renderPage(
      routedFetch({ providers: [provider("prov-1", "Alpha Forge")], repos: [] }, () => {
        const d = deferred<FakeResponse>();
        pending.push(d);
        return d.promise;
      }),
    );
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    const save = async () => {
      fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Edit" }));
      await screen.findByText("Edit Provider");
      fireEvent.click(screen.getByText("Update"));
      await waitFor(() => expect(screen.queryByText("Edit Provider")).toBeNull());
    };

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Test" }));
    pending[0].resolve(
      reply({ ok: true, outcome: "Ok", message: "Signed in as forge-bot.", detail: null }),
    );
    await within(card("Alpha Forge")).findByText("Signed in as forge-bot.");

    await save();
    await waitFor(() => expect(screen.queryByText("Signed in as forge-bot.")).toBeNull());

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Test" }));
    await within(card("Alpha Forge")).findByRole("button", { name: "Testing…" });
    await save();
    await within(card("Alpha Forge")).findByRole("button", { name: "Test" });

    pending[1].resolve(
      reply({ ok: false, outcome: "Unreachable", message: "Stale answer.", detail: "old" }),
    );
    await pending[1].promise;
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.queryByText("Stale answer.")).toBeNull();
  });

  test("saving a provider keeps its repositories' results", async () => {
    renderPage(
      routedFetch(
        {
          providers: [provider("prov-1", "Alpha Forge")],
          repos: [repo("repo-1", "alpha-app", "prov-1")],
        },
        undefined,
        () =>
          Promise.resolve(
            reply({ ok: true, outcome: "Ok", message: "Reached branch main.", detail: null }),
          ),
      ),
    );
    await waitFor(() => expect(screen.getByText("alpha-app")).toBeTruthy());

    fireEvent.click(within(repoRow("alpha-app")).getByRole("button", { name: "Test" }));
    await within(repoRow("alpha-app")).findByText("Reached branch main.");

    const providerEdit = within(card("Alpha Forge"))
      .getAllByRole("button", { name: "Edit" })
      .find((b) => !repoRow("alpha-app").contains(b))!;
    fireEvent.click(providerEdit);
    await screen.findByText("Edit Provider");
    fireEvent.click(screen.getByText("Update"));
    await waitFor(() => expect(screen.queryByText("Edit Provider")).toBeNull());

    expect(within(repoRow("alpha-app")).getByText("Reached branch main.")).toBeTruthy();
  });
});
