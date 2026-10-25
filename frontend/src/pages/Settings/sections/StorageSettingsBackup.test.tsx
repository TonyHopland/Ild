import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import StorageSettings from "./StorageSettings";

const MB = 1024 * 1024;
const REASON = "this tracker does not support backup";

type Status = {
  databases: Record<string, { available: boolean; reason?: string; preRestoreBackup?: unknown }>;
  secretKeyConfigured: boolean;
  minPassphraseLength: number;
};

const status = (overrides: Partial<Status> = {}): Status => ({
  databases: { core: { available: true }, workitems: { available: true } },
  secretKeyConfigured: true,
  minPassphraseLength: 12,
  ...overrides,
});

const overview = (extra: Record<string, unknown> = {}) => ({
  categories: [
    { key: "worktrees", label: "Worktrees", bytes: 2 * MB, incomplete: false, entries: [] },
    { key: "backups", label: "Backups", bytes: 3 * MB, incomplete: false, entries: [] },
  ],
  databases: [
    { key: "core", label: "ILD data", bytes: 5 * MB },
    { key: "workitems", label: "Work item data", bytes: null },
  ],
  ...extra,
});

type Call = { url: string; method: string; body: unknown };
let calls: Call[];
const createObjectURL = vi.fn(() => "blob:backup");

function serve(options: { status?: Status; overview?: unknown } = {}) {
  calls = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
    const method = init?.method ?? "GET";
    calls.push({ url, method, body: init?.body });
    if (url.endsWith("/storage") && method === "GET")
      return new Response(JSON.stringify(options.overview ?? overview()), { status: 200 });
    if (url.endsWith("/storage/backups") && method === "GET")
      return new Response(JSON.stringify(options.status ?? status()), { status: 200 });
    if (/\/storage\/backups\/[a-z]+$/.test(url) && method === "POST")
      return new Response(new Uint8Array([1, 2, 3]), {
        status: 200,
        headers: {
          "Content-Type": "application/octet-stream",
          "Content-Disposition": 'attachment; filename="ild-core-20260101-000000.ildbackup"',
        },
      });
    if (url.endsWith("/restore") && method === "POST")
      return new Response(JSON.stringify({}), { status: 200 });
    return new Response("{}", { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const posts = () => calls.filter((c) => c.method === "POST");
const pageText = () => document.body.textContent ?? "";
const backupSection = () =>
  screen.getByRole("heading", { name: /backup and restore/i }).closest("section") as HTMLElement;
const createButtons = () => screen.queryAllByRole("button", { name: /^create backup/i });
const restoreButtons = () => screen.queryAllByRole("button", { name: /^restore/i });

async function renderLoaded() {
  render(<StorageSettings />);
  await screen.findByRole("heading", { name: /backup and restore/i });
  await waitFor(() => expect(calls.some((c) => c.url.endsWith("/storage/backups"))).toBe(true));
  // The status has landed once a control (or the reason it is missing) is on the page.
  await waitFor(() =>
    expect(createButtons().length + screen.queryAllByText(REASON).length).toBeGreaterThan(0),
  );
}

beforeEach(() => {
  Object.defineProperty(URL, "createObjectURL", {
    value: vi.fn(() => "blob:backup"),
    configurable: true,
    writable: true,
  });
  Object.defineProperty(URL, "revokeObjectURL", {
    value: vi.fn(),
    configurable: true,
    writable: true,
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("Storage settings: Backup and restore", () => {
  test("is its own section, apart from the disk usage controls, and states the passphrase minimum", async () => {
    serve();
    await renderLoaded();

    const disk = screen.getByRole("heading", { name: /disk usage/i }).closest("section");
    expect(backupSection()).not.toBe(disk);
    expect(within(backupSection()).queryByRole("button", { name: /refresh/i })).toBeNull();
    expect(within(backupSection()).getAllByText(/12/).length).toBeGreaterThan(0);
  });

  test("shows the reason instead of controls when the work item backup is unavailable", async () => {
    serve({
      status: status({
        databases: { core: { available: true }, workitems: { available: false, reason: REASON } },
      }),
    });
    await renderLoaded();

    expect(within(backupSection()).getByText(REASON)).toBeTruthy();
    expect(createButtons()).toHaveLength(1);
    expect(restoreButtons()).toHaveLength(1);
  });

  test("without ILD_SECRET_KEY the core backup warns first and cannot start until the warning is acknowledged", async () => {
    serve({
      status: status({
        secretKeyConfigured: false,
        databases: { core: { available: true }, workitems: { available: false, reason: REASON } },
      }),
    });
    await renderLoaded();

    expect(within(backupSection()).getAllByText(/plaintext/i).length).toBeGreaterThan(0);
    expect(within(backupSection()).getAllByText(/ILD_SECRET_KEY/).length).toBeGreaterThan(0);
    fireEvent.change(screen.getAllByLabelText(/passphrase/i)[0], {
      target: { value: "a long enough passphrase" },
    });

    fireEvent.click(createButtons()[0]);
    expect(posts()).toHaveLength(0);

    fireEvent.click(screen.getByRole("checkbox"));
    fireEvent.click(createButtons()[0]);

    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0].url).toMatch(/\/storage\/backups\/core$/);
    expect(JSON.parse(posts()[0].body as string)).toEqual({
      passphrase: "a long enough passphrase",
    });
    await waitFor(() => expect(createObjectURL).toHaveBeenCalled());
  });

  test("with a secret key configured there is no plaintext warning and no acknowledgement to give", async () => {
    serve({
      status: status({
        databases: { core: { available: true }, workitems: { available: false, reason: REASON } },
      }),
    });
    await renderLoaded();

    expect(pageText()).not.toMatch(/plaintext/i);
    expect(screen.queryByRole("checkbox")).toBeNull();
    fireEvent.change(screen.getAllByLabelText(/passphrase/i)[0], {
      target: { value: "a long enough passphrase" },
    });
    fireEvent.click(createButtons()[0]);

    await waitFor(() => expect(posts()).toHaveLength(1));
  });

  test("a passphrase shorter than the minimum starts nothing", async () => {
    serve({
      status: status({
        databases: { core: { available: true }, workitems: { available: false, reason: REASON } },
      }),
    });
    await renderLoaded();

    fireEvent.change(screen.getAllByLabelText(/passphrase/i)[0], {
      target: { value: "too short" },
    });
    fireEvent.click(createButtons()[0]);

    expect(posts()).toHaveLength(0);
  });

  test("the restore warns that it replaces all data, that one database without the other can be inconsistent, and states the order", async () => {
    serve();
    await renderLoaded();

    const text = backupSection().textContent ?? "";
    expect(text).toMatch(/replaces all/i);
    expect(text).toMatch(/inconsisten/i);
    expect(text).toMatch(/paused/i);
    expect(text).toMatch(/work items first/i);
    expect(text).toMatch(/15 minutes/i);
  });

  test("a restore is sent only with the file, the passphrase and the typed confirmation", async () => {
    serve({
      status: status({
        databases: { core: { available: true }, workitems: { available: false, reason: REASON } },
      }),
    });
    await renderLoaded();
    const file = new File([new Uint8Array([9, 9, 9])], "ild-core.ildbackup", {
      type: "application/octet-stream",
    });
    const input = backupSection().querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });
    const passphrases = screen.getAllByLabelText(/passphrase/i);
    fireEvent.change(passphrases[passphrases.length - 1], {
      target: { value: "a long enough passphrase" },
    });

    fireEvent.click(restoreButtons()[0]);
    expect(posts()).toHaveLength(0);

    fireEvent.change(screen.getByLabelText(/type restore|confirm/i), {
      target: { value: "RESTORE" },
    });
    fireEvent.click(restoreButtons()[0]);

    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0].url).toMatch(/\/storage\/backups\/core\/restore$/);
    const form = posts()[0].body as FormData;
    expect(form.get("passphrase")).toBe("a long enough passphrase");
    expect(form.get("confirmation")).toBe("RESTORE");
    expect((form.get("file") as File).name).toBe("ild-core.ildbackup");
  });
});

describe("Storage settings: the overview", () => {
  test("shows each database's size when it is known and nothing for one that is not, and the space backups use", async () => {
    serve();
    render(<StorageSettings />);

    await screen.findByText("Backups");
    expect(screen.getByText("ILD data")).toBeTruthy();
    expect(pageText()).toContain("5 MB");
    expect(pageText()).toContain("3 MB");
    expect(pageText()).not.toMatch(/not measured/i);
  });
});
