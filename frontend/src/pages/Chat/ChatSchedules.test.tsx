import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import type { User } from "../../types";
import { openChatList } from "../../test-support";

const { handlers, authService } = vi.hoisted(() => ({
  handlers: {} as Record<string, Set<(msg: { payload: unknown }) => void>>,
  authService: {
    getUser: vi.fn(),
    getToken: vi.fn(() => "test-token"),
    getMe: vi.fn(),
    clearAuth: vi.fn(),
    logout: vi.fn(() => Promise.resolve()),
    onTokenChange: vi.fn(() => () => {}),
  },
}));

vi.mock("../../hooks/useSignalR", () => {
  const on = (event: string, handler: (msg: { payload: unknown }) => void) => {
    (handlers[event] ??= new Set()).add(handler);
  };
  const off = (event: string, handler: (msg: { payload: unknown }) => void) => {
    handlers[event]?.delete(handler);
  };
  return {
    useSignalR: () => ({ connectionState: "connected", on, off, invoke: () => Promise.resolve() }),
  };
});

vi.mock("../../services/auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../services/auth")>()),
  authService,
}));

import App from "../../App";
import ChatBubble from "../../components/ChatBubble";
import { ChatInboxProvider } from "../../components/ChatInbox";
import { AuthContext } from "../../hooks/useAuth";

const user: User = { id: "1", username: "test", createdAt: "" };

type Json = Record<string, unknown>;

/**
 * The API as the page reaches it: everything goes through fetch, so the page is
 * free to call it through whatever client functions it likes. Each request is
 * logged with its parsed body.
 */
let schedules: Json[] = [];
let history: Json[] = [];
let paused = false;
let nextCreate: { status: number; body: unknown }[] = [];
const requests: { method: string; path: string; body: Json | null }[] = [];

function respond(status: number, body: unknown) {
  const text = body === undefined ? "" : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: String(status),
    text: () => Promise.resolve(text),
    json: () => Promise.resolve(body),
    blob: () => Promise.resolve(new Blob([text])),
  };
}

function route(method: string, path: string, body: Json | null) {
  const schedule = /^\/api\/v1\/chat\/schedules\/([^/]+)$/.exec(path);
  const run = /^\/api\/v1\/chat\/schedules\/([^/]+)\/run$/.exec(path);
  const chat = /^\/api\/v1\/chat\/([^/]+)$/.exec(path);
  if (path === "/api/v1/chat/schedules" && method === "GET") return respond(200, schedules);
  if (path === "/api/v1/chat/schedules" && method === "POST") {
    const answer = nextCreate.shift() ?? { status: 201, body: undefined };
    if (answer.status !== 201) return respond(answer.status, answer.body);
    const created = { ...view("new", String(body?.name)), ...body, id: "new" };
    schedules = [...schedules, created];
    return respond(201, created);
  }
  if (schedule && method === "PUT") {
    schedules = schedules.map((s) => (s.id === schedule[1] ? { ...s, ...body } : s));
    return respond(
      200,
      schedules.find((s) => s.id === schedule[1]),
    );
  }
  if (schedule && method === "DELETE") {
    schedules = schedules.filter((s) => s.id !== schedule[1]);
    return respond(204, undefined);
  }
  if (run && method === "POST") {
    const firing = firingView("Running", null, 0);
    schedules = schedules.map((s) => (s.id === run[1] ? { ...s, lastFiring: firing } : s));
    return respond(200, firing);
  }
  if (path === "/api/v1/chat/history") return respond(200, history);
  if (path === "/api/v1/chat/search") return respond(200, []);
  if (path === "/api/v1/workitems/edit-proposals") return respond(200, []);
  if (chat && method === "GET") {
    const summary = history.find((c) => c.id === chat[1]);
    if (!summary) return respond(404, { error: "Chat not found." });
    return respond(200, {
      id: summary.id,
      name: summary.name,
      aiProviderId: "p1",
      providerType: "claude-code",
      tools: ["read", "write", "execute", "ild"],
      createdAt: "2026-07-06T06:00:00Z",
      messages: [],
      activeTurnId: null,
    });
  }
  if (path.startsWith("/api/v1/settings/")) {
    return respond(200, { key: "scheduler.isPaused", value: paused ? "true" : "false" });
  }
  if (path === "/api/v1/aiproviders") {
    return respond(200, [
      {
        id: "p1",
        name: "Claude",
        type: "claude-code",
        baseUrl: "",
        apiKey: "",
        model: "",
        isDefault: true,
        parallelism: 1,
        createdAt: "2026-01-01T00:00:00Z",
        tags: ["nightly"],
      },
    ]);
  }
  if (path === "/api/v1/repositories") {
    return respond(200, [
      { id: "r1", name: "example-repo", cloneUrl: "https://example.com/example-repo.git" },
      { id: "r2", name: "other-repo", cloneUrl: "https://example.com/other-repo.git" },
    ]);
  }
  return Promise.reject(new Error(`offline: ${method} ${path}`));
}

function firingView(
  outcome: string,
  reason: string | null,
  unresolvedItems: number,
  extra: Json = {},
): Json {
  return {
    id: `f-${Math.random()}`,
    trigger: "Schedule",
    scheduledFor: "2026-07-06T06:00:00Z",
    firedAt: "2026-07-06T06:00:05Z",
    endedAt: outcome === "Running" ? null : "2026-07-06T06:03:00Z",
    outcome,
    reason,
    chatSessionId: "c1",
    createdWorkItemIds: [],
    unresolvedItems,
    ...extra,
  };
}

function view(id: string, name: string, extra: Json = {}): Json {
  return {
    id,
    name,
    prompt: `Instructions for ${name}`,
    aiTag: "",
    cronExpression: "0 8 * * 1",
    timeZone: "Europe/Oslo",
    enabled: true,
    repositoryScope: "All",
    repositoryIds: [],
    continueSession: true,
    latestChatSessionId: null,
    nextFireAt: null,
    lastFiring: null,
    ...extra,
  };
}

function seed() {
  schedules = [
    view("s1", "Weekly retro", {
      aiTag: "nightly",
      cronExpression: "0 8 * * 1",
      repositoryScope: "Selected",
      repositoryIds: ["r1"],
      latestChatSessionId: "c1",
      nextFireAt: "2026-07-13T06:00:00Z",
      lastFiring: firingView("Skipped", "chat busy", 0),
    }),
    view("s2", "Dependency check", {
      aiTag: "",
      cronExpression: "15 3 1 * *",
      timeZone: "UTC",
      enabled: false,
      repositoryScope: "All",
      nextFireAt: null,
      lastFiring: firingView("Completed", null, 2, { createdWorkItemIds: ["WI-7"] }),
    }),
  ];
  history = [
    {
      id: "c1",
      name: "Retro chat",
      createdAt: "2026-07-06T06:00:00Z",
      updatedAt: "2026-07-06T06:03:00Z",
      hasUnread: false,
      scheduleId: "s1",
      scheduleName: "Weekly retro",
    },
    {
      id: "c2",
      name: "Plain chat",
      createdAt: "2026-07-01T06:00:00Z",
      updatedAt: "2026-07-01T06:03:00Z",
      hasUnread: false,
    },
  ];
}

beforeEach(() => {
  authService.getUser.mockReturnValue(user);
  authService.getMe.mockResolvedValue(user);
  localStorage.setItem("auth_token", "test-token");
  vi.stubGlobal(
    "fetch",
    vi.fn((input: string, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      const method = (init?.method ?? "GET").toUpperCase();
      const body =
        typeof init?.body === "string" && init.body ? (JSON.parse(init.body) as Json) : null;
      requests.push({ method, path: url.pathname, body });
      return Promise.resolve(route(method, url.pathname, body));
    }),
  );
});

afterEach(() => {
  cleanup();
  for (const k of Object.keys(handlers)) delete handlers[k];
  schedules = [];
  history = [];
  paused = false;
  nextCreate = [];
  requests.length = 0;
  vi.clearAllMocks();
  vi.restoreAllMocks();
  // Anything still in flight from the test just ended stays off the network.
  vi.stubGlobal(
    "fetch",
    vi.fn(() => Promise.reject(new Error("offline"))),
  );
  localStorage.clear();
  window.history.replaceState(null, "", "/");
});

function emit(event: string, payload: unknown) {
  act(() => {
    for (const handler of Array.from(handlers[event] ?? [])) handler({ payload });
  });
}

function renderAt(path: string) {
  window.history.replaceState(null, "", path);
  return render(<App />);
}

function pageSidebar() {
  return screen.getByRole("complementary", { name: "Chats" });
}

/** The schedules list's row for one schedule: the nearest row-like element around its name. */
async function scheduleRow(name: string): Promise<HTMLElement> {
  const main = await mainPane();
  const label = await within(main).findByText(name);
  const row = label.closest("tr, li, [role='row'], article");
  expect(row, `no row around ${name}`).not.toBeNull();
  return row as HTMLElement;
}

/** Everything on the page outside the Chat tab's sidebar and the main navigation. */
async function mainPane(): Promise<HTMLElement> {
  await screen.findByRole("complementary", { name: "Chats" });
  const sidebar = pageSidebar();
  const page = sidebar.parentElement as HTMLElement;
  const pane = Array.from(page.children).find(
    (child) => child !== sidebar && !child.contains(sidebar),
  ) as HTMLElement | undefined;
  expect(pane, "no main pane beside the sidebar").toBeTruthy();
  return pane!;
}

function toggleIn(row: HTMLElement): HTMLInputElement {
  return (within(row).queryByRole("switch") ??
    within(row).getByRole("checkbox")) as HTMLInputElement;
}

function isOn(toggle: HTMLElement): boolean {
  return (
    toggle.getAttribute("aria-checked") === "true" || (toggle as HTMLInputElement).checked === true
  );
}

/** Picks a preset however the form offers it: a button, a radio, or an option of a select. */
function choosePreset(dialog: HTMLElement, name: "Hourly" | "Daily" | "Weekly") {
  const pattern = new RegExp(`^${name}$`, "i");
  const button = within(dialog).queryByRole("button", { name: pattern });
  if (button) return fireEvent.click(button);
  const radio = within(dialog).queryByRole("radio", { name: pattern });
  if (radio) return fireEvent.click(radio);
  const option = within(dialog).getByRole("option", { name: pattern }) as HTMLOptionElement;
  fireEvent.change(option.closest("select")!, { target: { value: option.value } });
}

/** The cron expression's own text field, not a preset control that may share the word. */
function cronInput(dialog: HTMLElement): HTMLInputElement {
  const field = within(dialog)
    .getAllByLabelText(/cron/i)
    .find((el) => el instanceof HTMLInputElement && el.type === "text");
  expect(field, "no cron text field").toBeTruthy();
  return field as HTMLInputElement;
}

function lastRequest(method: string, path: string) {
  return [...requests].reverse().find((r) => r.method === method && r.path === path);
}

describe("The schedules list in the Chat tab", () => {
  test("/chat/schedules shows the Chat tab's sidebar with the schedules beside it, opens no chat and hides the bubble", async () => {
    seed();
    renderAt("/chat/schedules");

    await scheduleRow("Weekly retro");
    await scheduleRow("Dependency check");
    expect(window.location.pathname).toBe("/chat/schedules");
    expect(within(pageSidebar()).getByText("Retro chat")).toBeTruthy();
    expect(screen.queryByText("Start chat")).toBeNull();
    expect(screen.queryByText(/chat not found/i)).toBeNull();
    expect(screen.queryByRole("button", { name: "Open chat" })).toBeNull();
    expect(screen.queryByRole("dialog", { name: "AI chat" })).toBeNull();
  });

  test("the sidebar's Schedules entry leads from a chat to the list", async () => {
    seed();
    renderAt("/chat/c2");

    const entry = await within(
      await screen.findByRole("complementary", { name: "Chats" }),
    ).findByRole("link", { name: "Schedules" });
    expect(entry.getAttribute("href")).toBe("/chat/schedules");
    fireEvent.click(entry);

    await scheduleRow("Weekly retro");
    expect(window.location.pathname).toBe("/chat/schedules");
  });

  test("each row shows what the schedule is, when it fires and how it last went", async () => {
    seed();
    renderAt("/chat/schedules");

    const retro = await scheduleRow("Weekly retro");
    expect(retro.textContent).toContain("nightly");
    expect(retro.textContent).not.toContain("0 8 * * 1");
    expect(retro.textContent).toMatch(/monday/i);
    expect(retro.textContent).toContain("example-repo");
    expect(retro.textContent).not.toContain("other-repo");
    expect(isOn(toggleIn(retro))).toBe(true);
    expect(retro.textContent).toMatch(/skipped/i);
    expect(retro.textContent).toContain("chat busy");
    expect(within(retro).getByRole("link", { name: /chat/i }).getAttribute("href")).toBe(
      "/chat/c1",
    );
    expect(within(retro).getByRole("button", { name: /run now/i })).toBeTruthy();

    const deps = await scheduleRow("Dependency check");
    expect(deps.textContent).toMatch(/default/i);
    expect(deps.textContent).toMatch(/\ball\b/i);
    expect(isOn(toggleIn(deps))).toBe(false);
    expect(deps.textContent).toMatch(/completed/i);
    expect(deps.textContent).toMatch(/2\s*unresolved|unresolved\D{0,3}2\b/i);
    expect(deps.textContent).toMatch(/\bnone\b|\bnever\b|—/i);
    expect(within(deps).queryByRole("link", { name: /chat/i })).toBeNull();
  });

  test("the enabled toggle saves the schedule and Run now fires it", async () => {
    seed();
    renderAt("/chat/schedules");

    fireEvent.click(toggleIn(await scheduleRow("Weekly retro")));
    await waitFor(() => expect(lastRequest("PUT", "/api/v1/chat/schedules/s1")).toBeTruthy());
    const saved = lastRequest("PUT", "/api/v1/chat/schedules/s1")!.body!;
    expect(saved.enabled).toBe(false);
    expect(saved.name).toBe("Weekly retro");
    expect(saved.cronExpression).toBe("0 8 * * 1");
    expect(saved.aiTag).toBe("nightly");
    await waitFor(async () =>
      expect(isOn(toggleIn(await scheduleRow("Weekly retro")))).toBe(false),
    );

    fireEvent.click(
      within(await scheduleRow("Dependency check")).getByRole("button", { name: /run now/i }),
    );
    await waitFor(() => expect(lastRequest("POST", "/api/v1/chat/schedules/s2/run")).toBeTruthy());
    await waitFor(async () =>
      expect((await scheduleRow("Dependency check")).textContent).toMatch(/running/i),
    );
  });

  test("says while the scheduler is paused, and stops saying it the moment it resumes", async () => {
    seed();
    paused = true;
    renderAt("/chat/schedules");

    const main = await mainPane();
    await waitFor(() => expect(within(main).queryAllByText(/paused/i).length).toBeGreaterThan(0));

    paused = false;
    emit("SchedulerStateChanged", { isPaused: false, maxConcurrent: 5 });
    await waitFor(() => expect(within(main).queryAllByText(/paused/i)).toHaveLength(0));

    paused = true;
    emit("SchedulerStateChanged", { isPaused: true, maxConcurrent: 5 });
    await waitFor(() => expect(within(main).queryAllByText(/paused/i).length).toBeGreaterThan(0));
  });

  test("a new schedule is filled in from a preset in the browser's zone, and a refusal shows in the form", async () => {
    seed();
    const resolved = Intl.DateTimeFormat().resolvedOptions();
    vi.spyOn(Intl.DateTimeFormat.prototype, "resolvedOptions").mockReturnValue({
      ...resolved,
      timeZone: "America/New_York",
    });
    nextCreate = [{ status: 400, body: { error: "That cron never fires." } }];
    renderAt("/chat/schedules");
    const main = await mainPane();
    await scheduleRow("Weekly retro");

    fireEvent.click(
      within(main).getByRole("button", { name: /new schedule|add schedule|create schedule/i }),
    );
    const dialog = await screen.findByRole("dialog");
    const cron = cronInput(dialog);
    expect((within(dialog).getByLabelText(/time ?zone/i) as HTMLInputElement).value).toBe(
      "America/New_York",
    );

    choosePreset(dialog, "Hourly");
    expect(cron.value).toMatch(/^\d{1,2} \* \* \* \*$/);
    choosePreset(dialog, "Daily");
    expect(cron.value).toMatch(/^\d{1,2} \d{1,2} \* \* \*$/);
    choosePreset(dialog, "Weekly");
    expect(cron.value).toMatch(/^\d{1,2} \d{1,2} \* \* [0-7]$/);

    fireEvent.change(within(dialog).getByLabelText(/^name/i), { target: { value: "Docs drift" } });
    fireEvent.change(within(dialog).getByLabelText(/prompt/i), {
      target: { value: "Check the docs." },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: /^(save|create)/i }));

    expect(await within(dialog).findByText(/That cron never fires\./)).toBeTruthy();
    const refused = lastRequest("POST", "/api/v1/chat/schedules")!.body!;
    expect(refused.name).toBe("Docs drift");
    expect(refused.prompt).toBe("Check the docs.");
    expect(refused.timeZone).toBe("America/New_York");
    expect(refused.cronExpression).toBe(cron.value);

    fireEvent.click(within(dialog).getByRole("button", { name: /^(save|create)/i }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await scheduleRow("Docs drift");
  });

  test("a schedule is edited in the form and deleted only once confirmed", async () => {
    seed();
    renderAt("/chat/schedules");

    fireEvent.click(
      within(await scheduleRow("Weekly retro")).getByRole("button", { name: /edit/i }),
    );
    const dialog = await screen.findByRole("dialog");
    const name = within(dialog).getByLabelText(/^name/i) as HTMLInputElement;
    expect(name.value).toBe("Weekly retro");
    expect(cronInput(dialog).value).toBe("0 8 * * 1");
    fireEvent.change(name, { target: { value: "Monday retro" } });
    fireEvent.click(within(dialog).getByRole("button", { name: /^save/i }));
    await waitFor(() => expect(lastRequest("PUT", "/api/v1/chat/schedules/s1")).toBeTruthy());
    const saved = lastRequest("PUT", "/api/v1/chat/schedules/s1")!.body!;
    expect(saved.name).toBe("Monday retro");
    expect(saved.prompt).toBe("Instructions for Weekly retro");
    await scheduleRow("Monday retro");

    fireEvent.click(
      within(await scheduleRow("Dependency check")).getByRole("button", { name: /delete/i }),
    );
    const confirm = await screen.findByRole("alertdialog");
    expect(lastRequest("DELETE", "/api/v1/chat/schedules/s2")).toBeUndefined();
    fireEvent.click(within(confirm).getByRole("button", { name: /delete/i }));
    await waitFor(() => expect(lastRequest("DELETE", "/api/v1/chat/schedules/s2")).toBeTruthy());
    await waitFor(async () =>
      expect(within(await mainPane()).queryByText("Dependency check")).toBeNull(),
    );
  });
});

describe("Scheduled chats in the chat lists", () => {
  test("the Chat tab marks a scheduled chat, naming its schedule on hover", async () => {
    seed();
    renderAt("/chat/c2");

    const sidebar = await screen.findByRole("complementary", { name: "Chats" });
    const scheduled = (await within(sidebar).findByText("Retro chat")).closest("li") as HTMLElement;
    expect(within(scheduled).getByTitle(/Weekly retro/)).toBeTruthy();
    const plain = within(sidebar).getByText("Plain chat").closest("li") as HTMLElement;
    expect(within(plain).queryByTitle(/Weekly retro|schedule/i)).toBeNull();
  });

  test("the bubble gets no Schedules entry, no schedule controls and no marks", async () => {
    seed();
    const auth = {
      user,
      token: "test-token",
      isAuthenticated: true,
      isLoading: false,
      login: vi.fn(),
      logout: vi.fn(),
    };
    render(
      <MemoryRouter initialEntries={["/taskboard"]}>
        <AuthContext.Provider value={auth}>
          <ChatInboxProvider>
            <Routes>
              <Route path="*" element={<p>Another page</p>} />
            </Routes>
            <ChatBubble />
          </ChatInboxProvider>
        </AuthContext.Provider>
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Open chat" }));
    const list = await openChatList();
    const bubble = screen.getByRole("dialog", { name: "AI chat" });
    const scheduled = (await within(list).findByText("Retro chat")).closest("li") as HTMLElement;
    expect(within(scheduled).queryByTitle(/Weekly retro|schedule/i)).toBeNull();
    expect(within(scheduled).queryByLabelText(/Weekly retro|schedule/i)).toBeNull();
    expect(within(bubble).queryByText(/schedules?/i)).toBeNull();
    expect(within(bubble).queryAllByRole("link")).toEqual([]);
    expect(within(bubble).queryByRole("button", { name: /run now|schedule/i })).toBeNull();
  });
});
