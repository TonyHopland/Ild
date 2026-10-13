import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import GlobalVariables from "./index";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

interface Call {
  method: string;
  path: string;
  body: unknown;
}

const rules = {
  id: "g-1",
  name: "review_rules",
  value: "Keep reviews short.\nName the file.",
  updatedAt: "2026-10-01T10:00:00Z",
  usedBy: [
    { id: "tpl-1", name: "Dev Loop" },
    { id: "tpl-2", name: "Docs Loop" },
  ],
};

const writing = {
  id: "g-2",
  name: "writing",
  value: "Plain words.",
  updatedAt: "2026-10-02T10:00:00Z",
  usedBy: [],
};

type Reply = { status: number; body?: unknown };

/**
 * Answers the page's requests by method and path. `limit` is the stored
 * globalVariables.maxLength, or null for a setting that cannot be read.
 * `write` answers every POST, PUT and DELETE.
 */
function stubApi(options: {
  list: unknown[];
  limit?: string | null;
  write?: (call: Call) => Reply;
}) {
  const calls: Call[] = [];
  const fetchMock = vi.fn(async (input: string, init?: RequestInit) => {
    const url = new URL(input, "http://localhost");
    const method = (init?.method ?? "GET").toUpperCase();
    const call = {
      method,
      path: url.pathname,
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    };
    calls.push(call);
    let reply: Reply = { status: 404, body: { error: "not stubbed" } };
    if (method === "GET" && url.pathname === "/api/v1/global-variables") {
      reply = { status: 200, body: options.list };
    } else if (method === "GET" && url.pathname === "/api/v1/settings/globalVariables.maxLength") {
      reply =
        options.limit === null
          ? { status: 500, body: { error: "down" } }
          : {
              status: 200,
              body: { key: "globalVariables.maxLength", value: options.limit ?? "4000" },
            };
    } else if (method !== "GET" && options.write) {
      reply = options.write(call);
    }
    return {
      ok: reply.status < 400,
      status: reply.status,
      statusText: "",
      text: () => Promise.resolve(reply.body === undefined ? "" : JSON.stringify(reply.body)),
    } as Response;
  });
  vi.stubGlobal("fetch", fetchMock);
  return calls;
}

function renderPage() {
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
        <GlobalVariables />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

const writes = (calls: Call[]) => calls.filter((c) => c.method !== "GET");

/** The smallest element holding the variable's name: its row. */
async function rowOf(name: string) {
  const cell = await screen.findByText(name);
  let row = cell.parentElement!;
  while (!within(row).queryByRole("button", { name: /edit/i }) && row.parentElement) {
    row = row.parentElement;
  }
  return row;
}

/** Clicks a button and returns the buttons that were on the page before it opened the editor. */
function open(button: HTMLElement) {
  const before = new Set(screen.queryAllByRole("button"));
  fireEvent.click(button);
  return before;
}

/** Opens the editor for a new variable. */
async function openNew() {
  return open(await screen.findByRole("button", { name: /new|add|create/i }));
}

/** The editor's save button: the save-like button the editor brought with it. */
function saveButton(before: Set<HTMLElement>) {
  const found = screen
    .getAllByRole("button", { name: /save|create|update/i })
    .filter((b) => !before.has(b));
  expect(found).toHaveLength(1);
  return found[0];
}

function expectCount(count: number, limit: number) {
  expect(document.body.textContent).toMatch(new RegExp(`\\b${count}\\s*/\\s*${limit}\\b`));
}

describe("Global Variables page", () => {
  test("shows each variable's name, value and the templates using it, linking to them", async () => {
    stubApi({ list: [rules, writing] });
    renderPage();

    const row = await rowOf("review_rules");
    expect(row.textContent).toContain("Keep reviews short.");
    expect(row.textContent).toContain("Name the file.");
    expect(within(row).getByRole("link", { name: "Dev Loop" }).getAttribute("href")).toBe(
      "/loop-editor/tpl-1",
    );
    expect(within(row).getByRole("link", { name: "Docs Loop" }).getAttribute("href")).toBe(
      "/loop-editor/tpl-2",
    );

    const other = await rowOf("writing");
    expect(other.textContent).toContain("Plain words.");
    expect(within(other).queryAllByRole("link")).toHaveLength(0);
  });

  test("creates a variable, counting its characters against the limit", async () => {
    const calls = stubApi({
      list: [writing],
      limit: "20",
      write: () => ({ status: 201, body: { ...rules, usedBy: [] } }),
    });
    renderPage();
    await screen.findByText("writing");

    const before = await openNew();
    fireEvent.change(screen.getByLabelText(/name/i), { target: { value: "review_rules" } });
    fireEvent.change(screen.getByLabelText(/value/i), { target: { value: "Keep it short." } });
    expectCount(14, 20);
    fireEvent.click(saveButton(before));

    await waitFor(() => expect(writes(calls)).toHaveLength(1));
    expect(writes(calls)[0]).toEqual({
      method: "POST",
      path: "/api/v1/global-variables",
      body: { name: "review_rules", value: "Keep it short." },
    });
    // The list is read again after the save.
    await waitFor(() =>
      expect(
        calls.filter((c) => c.method === "GET" && c.path === "/api/v1/global-variables").length,
      ).toBeGreaterThan(1),
    );
  });

  test("counts against 4000 when the limit cannot be read", async () => {
    stubApi({ list: [], limit: null });
    renderPage();

    await openNew();
    fireEvent.change(screen.getByLabelText(/value/i), { target: { value: "abc" } });

    expectCount(3, 4000);
  });

  test("shows the server's refusal and keeps the dialog open", async () => {
    const refusal = "The value is 25 characters; the limit is 20.";
    stubApi({
      list: [writing],
      limit: "20",
      write: () => ({ status: 400, body: { error: refusal } }),
    });
    renderPage();
    await screen.findByText("writing");

    const before = await openNew();
    fireEvent.change(screen.getByLabelText(/name/i), { target: { value: "long_one" } });
    fireEvent.change(screen.getByLabelText(/value/i), { target: { value: "x".repeat(25) } });
    fireEvent.click(saveButton(before));

    expect(await screen.findByText(refusal)).toBeTruthy();
    expect((screen.getByLabelText(/value/i) as HTMLTextAreaElement).value).toBe("x".repeat(25));
  });

  test("edits only the value of an existing variable", async () => {
    const calls = stubApi({
      list: [rules],
      write: () => ({ status: 200, body: { ...rules, value: "Shorter." } }),
    });
    renderPage();

    const before = open(within(await rowOf("review_rules")).getByRole("button", { name: /edit/i }));
    const name = screen.queryByLabelText(/name/i) as HTMLInputElement | null;
    if (name) expect(name.readOnly || name.disabled).toBe(true);
    const value = screen.getByLabelText(/value/i) as HTMLTextAreaElement;
    expect(value.value).toBe(rules.value);
    fireEvent.change(value, { target: { value: "Shorter." } });
    expectCount(8, 4000);
    fireEvent.click(saveButton(before));

    await waitFor(() => expect(writes(calls)).toHaveLength(1));
    expect(writes(calls)[0]).toEqual({
      method: "PUT",
      path: "/api/v1/global-variables/g-1",
      body: { value: "Shorter." },
    });
  });

  test("deletes a variable only after the delete is confirmed", async () => {
    const calls = stubApi({ list: [rules, writing], write: () => ({ status: 204 }) });
    renderPage();

    const row = await rowOf("review_rules");
    fireEvent.click(within(row).getByRole("button", { name: /delete/i }));
    expect(writes(calls)).toHaveLength(0);

    fireEvent.click(within(row).getByRole("button", { name: /confirm|yes/i }));

    await waitFor(() => expect(writes(calls)).toHaveLength(1));
    expect(writes(calls)[0]).toMatchObject({
      method: "DELETE",
      path: "/api/v1/global-variables/g-1",
    });
  });
});
