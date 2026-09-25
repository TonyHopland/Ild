import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import RemoteProviders from "./index";

const availableTypes = [{ type: "Forgejo" }, { type: "GitHub" }];

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
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(availableTypes)),
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
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(availableTypes)),
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
      name: "GitHub",
      type: "GitHub",
      baseUrl: "https://github.com",
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
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(availableTypes)),
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

    expect(screen.queryByRole("option", { name: "GitLab" })).toBeFalsy();
    expect((screen.getByLabelText("Webhook Secret") as HTMLInputElement).value).not.toBe("");

    // Fill in required fields
    fireEvent.change(screen.getByLabelText("Name"), {
      target: { value: "GitHub" },
    });

    const typeSelect = screen.getByLabelText("Type");
    fireEvent.change(typeSelect, {
      target: { value: "GitHub" },
    });

    fireEvent.change(screen.getByLabelText("Base URL"), {
      target: { value: "https://github.com" },
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
          text: () => Promise.resolve(JSON.stringify(availableTypes)),
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

    const postCall = fetchMock.mock.calls.find(([, options]) => options?.method === "POST");
    expect(postCall).toBeTruthy();
    const postBody = JSON.parse(String(postCall?.[1]?.body ?? "{}"));
    expect(postBody.webhookSecret).toBeTruthy();
  });

  test("edit form pre-fills fields and calls update API on save", async () => {
    const providers = [
      {
        id: "prov-1",
        name: "Forgejo",
        type: "Forgejo",
        baseUrl: "https://git.example.com",
        apiKey: "old-key",
        hasApiKey: true,
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
    fetchMock.mockReturnValueOnce(
      Promise.resolve({
        ok: true,
        status: 200,
        text: () => Promise.resolve(JSON.stringify(availableTypes)),
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
    expect((screen.getByLabelText("API Key") as HTMLInputElement).placeholder).toBe("(unchanged)");
    expect((screen.getByLabelText("Webhook Secret") as HTMLInputElement).placeholder).toBe(
      "(unchanged)",
    );

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
      )
      .mockReturnValueOnce(
        Promise.resolve({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(availableTypes)),
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

  test("webhook secret can be regenerated in the form", async () => {
    const providers: unknown[] = [];

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
        text: () => Promise.resolve(JSON.stringify(availableTypes)),
      }),
    );

    renderPage(fetchMock);

    await waitFor(() => {
      expect(screen.getByText("Remote Providers")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("+ New Provider"));

    const webhookSecretInput = screen.getByLabelText("Webhook Secret") as HTMLInputElement;
    const initialValue = webhookSecretInput.value;
    fireEvent.click(screen.getByText("Generate"));

    expect(webhookSecretInput.value).not.toBe("");
    expect(webhookSecretInput.value).not.toBe(initialValue);
  });
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

/**
 * Answers the page's list/type/update calls from `state`, and hands every test
 * call to `onTest` so a test decides when (and how) each one settles.
 */
function routedFetch(
  state: { providers: Record<string, unknown>[] },
  onTest: (id: string) => Promise<FakeResponse>,
) {
  return vi.fn().mockImplementation((url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const test = /\/remoteproviders\/([^/]+)\/test$/.exec(url);
    if (test && method === "POST") return onTest(test[1]);
    if (url.includes("/remoteproviders/types")) return Promise.resolve(reply(availableTypes));
    const one = /\/remoteproviders\/([^/?]+)$/.exec(url);
    if (one && method === "PUT") {
      state.providers = state.providers.map((p) =>
        p.id === one[1] ? { ...p, updatedAt: new Date(Date.now() + 60_000).toISOString() } : p,
      );
      return Promise.resolve(reply(state.providers.find((p) => p.id === one[1])));
    }
    if (url.includes("/remoteproviders")) return Promise.resolve(reply(state.providers));
    return Promise.resolve(reply(null, 404));
  });
}

function provider(id: string, name: string) {
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
  };
}

function card(name: string): HTMLElement {
  return screen.getByText(name).closest(".rp-card") as HTMLElement;
}

describe("Remote Providers page — Test button", () => {
  test("each card tests its own provider and shows the result in that card only", async () => {
    const state = {
      providers: [provider("prov-1", "Alpha Forge"), provider("prov-2", "Beta Forge")],
    };
    const pending: Record<string, ReturnType<typeof deferred<FakeResponse>>> = {};
    const fetchMock = routedFetch(state, (id) => {
      pending[id] = deferred<FakeResponse>();
      return pending[id].promise;
    });
    renderPage(fetchMock);
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    expect(within(card("Alpha Forge")).getByRole("button", { name: "Test" })).toBeTruthy();
    expect(within(card("Beta Forge")).getByRole("button", { name: "Test" })).toBeTruthy();

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
    expect(
      (within(card("Alpha Forge")).getByRole("button", { name: "Test" }) as HTMLButtonElement)
        .disabled,
    ).toBe(false);

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
    expect(within(card("Alpha Forge")).queryByText("The API key was rejected.")).toBeNull();
  });

  test("a call to ILD that fails says the test couldn't run, with the error", async () => {
    const state = { providers: [provider("prov-1", "Alpha Forge")] };
    const answers: Array<() => Promise<FakeResponse>> = [
      () => Promise.reject(new TypeError("Failed to fetch")),
      () => Promise.resolve(reply({ error: "boom" }, 500)),
    ];
    renderPage(routedFetch(state, () => answers.shift()!()));
    await waitFor(() => expect(screen.getByText("Alpha Forge")).toBeTruthy());

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Test" }));
    await within(card("Alpha Forge")).findByText("Couldn't run the test: Failed to fetch");

    fireEvent.click(within(card("Alpha Forge")).getByRole("button", { name: "Test" }));
    await within(card("Alpha Forge")).findByText("Couldn't run the test: boom");
    expect(within(card("Alpha Forge")).queryByText(/Failed to fetch/)).toBeNull();
  });

  test("saving a provider clears its result, and drops one still in flight", async () => {
    const state = { providers: [provider("prov-1", "Alpha Forge")] };
    const pending: Array<ReturnType<typeof deferred<FakeResponse>>> = [];
    renderPage(
      routedFetch(state, () => {
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
});
