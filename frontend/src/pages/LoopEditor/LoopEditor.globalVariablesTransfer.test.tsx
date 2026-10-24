import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { EdgeType, NodeType, RecoveryPolicy } from "../../types";

// Exporting a loop can carry the global variables it uses; importing one creates
// them, or asks before replacing one whose value differs. The ILD API is faked
// at the HTTP boundary so the real services run.

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "connected",
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(() => Promise.resolve()),
  }),
}));

import LoopEditor from "./index";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

type Call = { method: string; path: string; body: any };
type Stored = { id: string; name: string; value: string };

interface Server {
  templates?: unknown[];
  globals?: Stored[];
  failGlobalsList?: boolean;
  /** Variable writes the server refuses, by variable name. */
  refuse?: Record<string, string>;
  failTemplateWrites?: boolean;
}

function respond(status: number, json: unknown) {
  return Promise.resolve({
    ok: status < 400,
    status,
    statusText: status < 400 ? "OK" : "Error",
    text: () => Promise.resolve(json === undefined ? "" : JSON.stringify(json)),
  });
}

function serve(server: Server) {
  const calls: Call[] = [];
  const globals = (server.globals ??= []);
  let nextId = 100;
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    const method = (init?.method ?? "GET").toUpperCase();
    const path = String(url).replace(/^.*\/api\/v1/, "");
    const body = typeof init?.body === "string" ? JSON.parse(init.body) : undefined;
    calls.push({ method, path, body });

    if (path === "/global-variables" && method === "GET") {
      return server.failGlobalsList
        ? respond(500, { error: "boom" })
        : respond(
            200,
            globals.map((g) => ({ ...g, updatedAt: "2026-10-01T10:00:00Z", usedBy: [] })),
          );
    }
    if (path === "/global-variables" && method === "POST") {
      const refusal = server.refuse?.[body.name];
      if (refusal) return respond(400, { error: refusal });
      if (globals.some((g) => g.name.toLowerCase() === body.name.toLowerCase()))
        return respond(400, { error: "name taken" });
      const created = { id: `g-${nextId++}`, name: body.name, value: body.value };
      globals.push(created);
      return respond(201, { ...created, updatedAt: "", usedBy: [] });
    }
    if (path.startsWith("/global-variables/") && method === "PUT") {
      const target = globals.find((g) => g.id === path.split("/")[2]);
      if (!target) return respond(404, { error: "gone" });
      const refusal = server.refuse?.[target.name];
      if (refusal) return respond(400, { error: refusal });
      target.value = body.value;
      return respond(200, { ...target, updatedAt: "", usedBy: [] });
    }
    if (method === "GET" && path === "/looptemplates/node-outputs") return respond(200, {});
    if (method === "GET" && /^\/looptemplates(\?|$)/.test(path))
      return respond(200, server.templates ?? []);
    if (method === "POST" && path === "/looptemplates/validate")
      return respond(200, { valid: true, errors: [] });
    if (method === "POST" && path === "/looptemplates/upgrade-document")
      return respond(200, { document: body.document });
    if (method === "POST" && path === "/looptemplates")
      return server.failTemplateWrites
        ? respond(500, { error: "nope" })
        : respond(200, { id: "n" });
    if (method === "PUT" && path.startsWith("/looptemplates/"))
      return server.failTemplateWrites ? respond(500, { error: "nope" }) : respond(200, {});
    return respond(200, null);
  });
  vi.stubGlobal("fetch", fetchMock);
  return calls;
}

const authValue = {
  user: { id: "1", username: "test", createdAt: "" },
  token: "test-token",
  isAuthenticated: true,
  isLoading: false,
  login: vi.fn(),
  logout: vi.fn(),
};

function renderEditor(path: string) {
  render(
    <MemoryRouter initialEntries={[path]}>
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor" element={<LoopEditor />} />
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
}

const downloads: Blob[] = [];

function readBlob(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error);
    reader.readAsText(blob);
  });
}

async function downloaded(index = 0): Promise<any> {
  return JSON.parse(await readBlob(downloads[index]));
}

vi.stubGlobal("URL", {
  ...URL,
  createObjectURL: (blob: Blob) => {
    downloads.push(blob);
    return "blob:test";
  },
  revokeObjectURL: () => {},
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
  downloads.length = 0;
});

const stored = (name: string, value: string, id = `g-${name}`): Stored => ({ id, name, value });

function loopWith(...prompts: string[]) {
  return {
    id: "tpl-1",
    name: "Dev Loop",
    description: "",
    version: 1,
    recoveryPolicy: RecoveryPolicy.AutoResume,
    nodes: [
      { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
      ...prompts.map((prompt, i) => ({
        id: `n-ai-${i}`,
        type: NodeType.AI,
        label: `Reviewer ${i}`,
        config: { prompt },
      })),
      { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
    ],
    edges: [],
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
    isArchived: false,
  };
}

async function openLoop(server: Server) {
  const calls = serve(server);
  renderEditor("/loop-editor/tpl-1");
  await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());
  await act(async () => {});
  await act(async () => {});
  return calls;
}

const clickExport = () => fireEvent.click(screen.getByTitle("Export template to JSON"));
const exportDialog = () => screen.queryByRole("dialog", { name: "Include global variables" });
const globalRequests = (calls: Call[], from = 0) =>
  calls.slice(from).filter((c) => c.path.startsWith("/global-variables"));

describe("Loop Editor — exporting global variables", () => {
  test("a loop that uses none downloads at once, without the key and without asking the server", async () => {
    const calls = await openLoop({
      templates: [loopWith("plain {{Session.x}} text")],
      globals: [stored("tone", "calm")],
    });
    const before = calls.length;

    clickExport();

    await waitFor(() => expect(downloads).toHaveLength(1));
    expect(exportDialog()).toBeNull();
    expect("globalVariables" in (await downloaded())).toBe(false);
    expect(globalRequests(calls, before)).toEqual([]);
  });

  test("a failed fetch of the variables shows an error and downloads nothing", async () => {
    const server: Server = { templates: [loopWith("{{Global.tone}}")], globals: [] };
    await openLoop(server);
    server.failGlobalsList = true;

    clickExport();

    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(downloads).toHaveLength(0);
    expect(exportDialog()).toBeNull();
  });

  test("when none of the used names exists the file downloads without a dialog", async () => {
    await openLoop({
      templates: [loopWith("{{Global.ghost}}")],
      globals: [stored("tone", "calm")],
    });

    clickExport();

    await waitFor(() => expect(downloads).toHaveLength(1));
    expect(exportDialog()).toBeNull();
    expect("globalVariables" in (await downloaded())).toBe(false);
  });

  const setup = () =>
    openLoop({
      templates: [
        loopWith("{{Global.WRITING}} and {{global.Tone}}", "{{Global.ghost}} {{Global.tone}}"),
      ],
      globals: [stored("Tone", "calm"), stored("writing", "be brief"), stored("unused", "x")],
    });

  test("lists only the used variables that exist, by stored name", async () => {
    await setup();

    clickExport();

    const dialog = await screen.findByRole("dialog", { name: "Include global variables" });
    expect(within(dialog).getByText("writing")).toBeTruthy();
    expect(within(dialog).getByText("Tone")).toBeTruthy();
    expect(within(dialog).queryByText(/ghost/)).toBeNull();
    expect(within(dialog).queryByText(/unused/)).toBeNull();
    expect(downloads).toHaveLength(0);
  });

  test("Include downloads the variables with stored names, in order of first appearance", async () => {
    await setup();
    clickExport();
    const dialog = await screen.findByRole("dialog", { name: "Include global variables" });

    fireEvent.click(within(dialog).getByRole("button", { name: "Include variables" }));

    await waitFor(() => expect(downloads).toHaveLength(1));
    const file = await downloaded();
    expect(file.globalVariables).toEqual([
      { name: "writing", value: "be brief" },
      { name: "Tone", value: "calm" },
    ]);
    expect(file.$schema).toBe("ild-loop-template/v2");
    expect(file.name).toBe("Dev Loop");
    expect(file.nodes).toHaveLength(4);
    expect(exportDialog()).toBeNull();
  });

  test("Export without downloads the file with no globalVariables key", async () => {
    await setup();
    clickExport();
    const dialog = await screen.findByRole("dialog", { name: "Include global variables" });

    fireEvent.click(within(dialog).getByRole("button", { name: "Export without" }));

    await waitFor(() => expect(downloads).toHaveLength(1));
    const file = await downloaded();
    expect("globalVariables" in file).toBe(false);
    expect(file.$schema).toBe("ild-loop-template/v2");
    expect(exportDialog()).toBeNull();
  });

  test.each([
    [
      "the Cancel button",
      (d: HTMLElement) => fireEvent.click(within(d).getByRole("button", { name: "Cancel" })),
    ],
    ["Escape", (d: HTMLElement) => fireEvent.keyDown(d, { key: "Escape" })],
    ["a click on the overlay", (d: HTMLElement) => fireEvent.mouseDown(d.parentElement!)],
  ])("%s cancels and downloads nothing", async (_label, cancel) => {
    await setup();
    clickExport();
    const dialog = await screen.findByRole("dialog", { name: "Include global variables" });

    cancel(dialog);

    await waitFor(() => expect(exportDialog()).toBeNull());
    await act(async () => {});
    expect(downloads).toHaveLength(0);
  });
});

describe("Loop Editor — importing global variables", () => {
  const importedLoop = (name: string, globalVariables?: unknown) =>
    JSON.stringify({
      $schema: "ild-loop-template/v2",
      name,
      description: "",
      recoveryPolicy: RecoveryPolicy.AutoResume,
      nodes: [
        { id: "s", type: NodeType.Start, label: "Start", config: {} },
        { id: "c", type: NodeType.Cleanup, label: "Cleanup", config: {} },
      ],
      edges: [
        {
          id: "e1",
          sourceNodeId: "s",
          targetNodeId: "c",
          edgeType: EdgeType.OnSuccess,
          name: null,
        },
      ],
      ...(globalVariables === undefined ? {} : { globalVariables }),
    });

  const existingTemplate = (name: string) => ({ ...loopWith(), id: `tpl-${name}`, name });

  async function startImport(server: Server, files: [string, string][]) {
    const calls = serve(server);
    renderEditor("/loop-editor");
    await act(async () => {});
    await act(async () => {});
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    const picked = files.map(
      ([name, content]) => new File([content], name, { type: "application/json" }),
    );
    fireEvent.change(input, { target: { files: picked } });
    return calls;
  }

  const conflictDialog = () => screen.queryByRole("dialog", { name: "Global variable conflict" });
  const templateDialog = () => screen.queryByRole("dialog", { name: "Import conflict" });
  const writes = (calls: Call[]) =>
    calls.filter(
      (c) => c.path.startsWith("/global-variables") && (c.method === "POST" || c.method === "PUT"),
    );
  const templateWrites = (calls: Call[]) =>
    calls.filter(
      (c) =>
        c.path.startsWith("/looptemplates") &&
        !c.path.includes("validate") &&
        !c.path.includes("upgrade") &&
        !c.path.includes("node-outputs") &&
        (c.method === "POST" || c.method === "PUT"),
    );

  async function results() {
    const panel = (await screen.findByText("Import Results")).closest(".import-feedback-panel")!;
    return Array.from(panel.querySelectorAll(".import-feedback-item")).map((row) => ({
      status: ["success", "error", "skipped"].find((s) =>
        row.classList.contains(`import-feedback-${s}`),
      ),
      file: row.querySelector(".import-feedback-filename")!.textContent!,
      text: row.querySelector(".import-feedback-message")!.textContent!,
    }));
  }

  test("creates missing variables, leaves identical ones alone, and lists them after the loop line", async () => {
    const server: Server = { globals: [stored("Same", "v")] };
    const calls = await startImport(server, [
      [
        "a.json",
        importedLoop("Alpha", [
          { name: "fresh", value: "new value" },
          { name: "same", value: "v" },
        ]),
      ],
    ]);

    const rows = await results();

    expect(writes(calls).map((c) => [c.method, c.path, c.body])).toEqual([
      ["POST", "/global-variables", { name: "fresh", value: "new value" }],
    ]);
    expect(rows).toHaveLength(2);
    expect(rows[0].text).toMatch(/Created/);
    expect(rows[1].status).toBe("success");
    expect(rows[1].text).toContain("fresh");
  });

  test("a differing variable shows a diff of current against imported and Overwrite writes the import, keeping the stored name", async () => {
    const server: Server = { globals: [stored("Tone", "calm")] };
    const calls = await startImport(server, [
      ["a.json", importedLoop("Alpha", [{ name: "tone", value: "loud" }])],
    ]);

    const dialog = await screen.findByRole("dialog", { name: "Global variable conflict" });
    expect(dialog.textContent).toContain("tone");
    const diff = within(dialog).getByTestId("global-variable-diff");
    expect(diff.querySelector(".save-diff-del")?.textContent).toContain("calm");
    expect(diff.querySelector(".save-diff-add")?.textContent).toContain("loud");
    expect(writes(calls)).toEqual([]);

    fireEvent.click(within(dialog).getByRole("button", { name: "Overwrite" }));

    const rows = await results();
    expect(writes(calls).map((c) => [c.method, c.path, c.body])).toEqual([
      ["PUT", "/global-variables/g-Tone", { value: "loud" }],
    ]);
    expect(server.globals![0].name).toBe("Tone");
    expect(rows.map((r) => r.status)).toEqual(["success", "success"]);
  });

  test.each([
    [
      "Keep current",
      (d: HTMLElement) => fireEvent.click(within(d).getByRole("button", { name: "Keep current" })),
    ],
    ["Escape", (d: HTMLElement) => fireEvent.keyDown(d, { key: "Escape" })],
    ["a click on the overlay", (d: HTMLElement) => fireEvent.mouseDown(d.parentElement!)],
  ])("%s keeps the current value and reports it as skipped", async (_label, keep) => {
    const server: Server = { globals: [stored("tone", "calm")] };
    const calls = await startImport(server, [
      ["a.json", importedLoop("Alpha", [{ name: "tone", value: "loud" }])],
    ]);
    const dialog = await screen.findByRole("dialog", { name: "Global variable conflict" });

    keep(dialog);

    const rows = await results();
    expect(writes(calls)).toEqual([]);
    expect(server.globals![0].value).toBe("calm");
    expect(rows.map((r) => r.status)).toEqual(["success", "skipped"]);
  });

  test("conflicts are asked one at a time, in file order, and later files wait for the answer", async () => {
    const server: Server = { globals: [stored("one", "1"), stored("two", "2")] };
    const calls = await startImport(server, [
      [
        "a.json",
        importedLoop("Alpha", [
          { name: "one", value: "1b" },
          { name: "two", value: "2b" },
        ]),
      ],
      ["b.json", importedLoop("Beta", [{ name: "fresh", value: "f" }])],
    ]);

    const dialog = await screen.findByRole("dialog", { name: "Global variable conflict" });
    expect(dialog.textContent).toContain("one");
    expect(screen.getAllByRole("dialog")).toHaveLength(1);
    expect(templateWrites(calls).map((c) => c.body.name)).toEqual(["Alpha"]);

    fireEvent.click(within(dialog).getByRole("button", { name: "Overwrite" }));
    await waitFor(() => expect(conflictDialog()?.textContent).toContain("two"));
    expect(templateWrites(calls).map((c) => c.body.name)).toEqual(["Alpha"]);

    fireEvent.click(within(conflictDialog()!).getByRole("button", { name: "Keep current" }));
    const rows = await results();

    expect(templateWrites(calls).map((c) => c.body.name)).toEqual(["Alpha", "Beta"]);
    expect(rows.map((r) => [r.file, r.status])).toEqual([
      ["a.json", "success"],
      ["a.json", "success"],
      ["a.json", "skipped"],
      ["b.json", "success"],
      ["b.json", "success"],
    ]);
    expect(writes(calls).map((c) => [c.method, c.body])).toEqual([
      ["PUT", { value: "1b" }],
      ["POST", { name: "fresh", value: "f" }],
    ]);
  });

  test("a template conflict answered Update closes that dialog before the variable diff, and the next file waits", async () => {
    const server: Server = {
      templates: [existingTemplate("Alpha")],
      globals: [stored("tone", "calm")],
    };
    const calls = await startImport(server, [
      ["a.json", importedLoop("Alpha", [{ name: "tone", value: "loud" }])],
      ["b.json", importedLoop("Beta")],
    ]);

    const templateConflict = await screen.findByRole("dialog", { name: "Import conflict" });
    fireEvent.click(within(templateConflict).getByRole("button", { name: "Update" }));

    const diffDialog = await screen.findByRole("dialog", { name: "Global variable conflict" });
    expect(templateDialog()).toBeNull();
    expect(screen.getAllByRole("dialog")).toHaveLength(1);
    expect(templateWrites(calls).map((c) => [c.method, c.body?.name])).toEqual([["PUT", "Alpha"]]);

    fireEvent.click(within(diffDialog).getByRole("button", { name: "Overwrite" }));
    const rows = await results();

    expect(templateWrites(calls).map((c) => [c.method, c.body?.name])).toEqual([
      ["PUT", "Alpha"],
      ["POST", "Beta"],
    ]);
    expect(rows.map((r) => [r.file, r.status])).toEqual([
      ["a.json", "success"],
      ["a.json", "success"],
      ["b.json", "success"],
    ]);
    expect(rows[0].text).toMatch(/Updated/);
  });

  test("a template conflict answered Skip imports none of the file's variables", async () => {
    const server: Server = { templates: [existingTemplate("Alpha")], globals: [] };
    const calls = await startImport(server, [
      ["a.json", importedLoop("Alpha", [{ name: "fresh", value: "f" }])],
    ]);

    const templateConflict = await screen.findByRole("dialog", { name: "Import conflict" });
    fireEvent.click(within(templateConflict).getByRole("button", { name: "Skip" }));

    const rows = await results();
    expect(rows).toHaveLength(1);
    expect(rows[0].status).toBe("skipped");
    expect(writes(calls)).toEqual([]);
  });

  test("a failed loop write imports none of the file's variables", async () => {
    const calls = await startImport({ failTemplateWrites: true }, [
      ["a.json", importedLoop("Alpha", [{ name: "fresh", value: "f" }])],
    ]);

    const rows = await results();
    expect(rows.map((r) => r.status)).toEqual(["error"]);
    expect(writes(calls)).toEqual([]);
  });

  test.each([
    ["not an array", { name: "a", value: "b" }],
    ["a bad name", [{ name: "1bad", value: "b" }]],
    ["a non-string value", [{ name: "ok", value: 3 }]],
    [
      "names equal ignoring case",
      [
        { name: "Ab", value: "1" },
        { name: "aB", value: "2" },
      ],
    ],
  ])("a file with %s is rejected and the next file still imports", async (_label, bad) => {
    const calls = await startImport({ globals: [] }, [
      ["bad.json", importedLoop("Bad", bad)],
      ["good.json", importedLoop("Good", [{ name: "fresh", value: "f" }])],
    ]);

    const rows = await results();

    expect(rows[0].file).toBe("bad.json");
    expect(rows[0].status).toBe("error");
    expect(rows[0].text).toMatch(/globalVariables/);
    expect(templateWrites(calls).map((c) => c.body.name)).toEqual(["Good"]);
    expect(writes(calls).map((c) => c.body)).toEqual([{ name: "fresh", value: "f" }]);
    expect(rows.slice(1).map((r) => [r.file, r.status])).toEqual([
      ["good.json", "success"],
      ["good.json", "success"],
    ]);
  });

  test("a refused write is reported with the server's message and the rest continues", async () => {
    const server: Server = {
      globals: [stored("tone", "calm")],
      refuse: { big: "Value is too long", tone: "Variable was removed" },
    };
    const calls = await startImport(server, [
      [
        "a.json",
        importedLoop("Alpha", [
          { name: "big", value: "x" },
          { name: "tone", value: "loud" },
          { name: "last", value: "l" },
        ]),
      ],
      ["b.json", importedLoop("Beta")],
    ]);
    const dialog = await screen.findByRole("dialog", { name: "Global variable conflict" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Overwrite" }));

    const rows = await results();

    expect(rows.map((r) => [r.file, r.status])).toEqual([
      ["a.json", "success"],
      ["a.json", "error"],
      ["a.json", "error"],
      ["a.json", "success"],
      ["b.json", "success"],
    ]);
    expect(rows[1].text).toContain("Value is too long");
    expect(rows[2].text).toContain("Variable was removed");
    expect(writes(calls).map((c) => c.body.name ?? c.body.value)).toEqual(["big", "loud", "last"]);
  });

  test("files picked while a variable diff is open are ignored and the running import finishes", async () => {
    const server: Server = { globals: [stored("tone", "calm")] };
    const calls = await startImport(server, [
      ["a.json", importedLoop("Alpha", [{ name: "tone", value: "loud" }])],
      ["b.json", importedLoop("Beta")],
    ]);
    const dialog = await screen.findByRole("dialog", { name: "Global variable conflict" });

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, {
      target: { files: [new File([importedLoop("Gamma")], "c.json")] },
    });
    await act(async () => {});
    expect(templateWrites(calls).map((c) => c.body.name)).toEqual(["Alpha"]);

    fireEvent.click(within(dialog).getByRole("button", { name: "Overwrite" }));
    const rows = await results();

    expect(templateWrites(calls).map((c) => c.body.name)).toEqual(["Alpha", "Beta"]);
    expect(rows.map((r) => r.file)).toEqual(["a.json", "a.json", "b.json"]);
  });

  test("a file without globalVariables makes no global-variable requests during the import", async () => {
    const calls = serve({ globals: [] });
    renderEditor("/loop-editor");
    await act(async () => {});
    await act(async () => {});
    const before = calls.length;
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, {
      target: { files: [new File([importedLoop("Plain")], "p.json")] },
    });

    const rows = await results();

    expect(rows).toHaveLength(1);
    expect(rows[0].text).toMatch(/Created/);
    expect(globalRequests(calls, before)).toEqual([]);
  });
});
