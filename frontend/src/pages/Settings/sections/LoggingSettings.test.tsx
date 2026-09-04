import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import LoggingSettings from "./LoggingSettings";
import * as signalRHook from "../../../hooks/useSignalR";
import * as authServices from "../../../services/auth";
import type { LogEntry } from "../../../types";

type Handler = (message: { type: string; payload: unknown; timestamp: string }) => void;
const handlers = new Map<string, Set<Handler>>();

function emit(type: string, payload: unknown) {
  handlers.get(type)?.forEach((h) => h({ type, payload, timestamp: new Date().toISOString() }));
}

const levelButton = (name: string) =>
  within(screen.getByRole("group", { name: "Log level override" })).getByRole("button", { name });

const entry = (over: Partial<LogEntry> = {}): LogEntry => ({
  id: 1,
  timestamp: "2026-09-03T09:00:00Z",
  level: "Information",
  source: "ILD.Core.LoopEngine",
  message: "Run 7f21 entered node 'Implement'",
  detail: null,
  ...over,
});

const warning = entry({
  id: 2,
  level: "Warning",
  source: "ILD.Core.Remote.PrStatusPoller",
  message: "GitHub rate limit at 12% remaining",
});
const failure = entry({
  id: 3,
  level: "Error",
  source: "ILD.Core.Executors.AINodeExecutor",
  message: "Node 'Review' failed: provider returned 529",
  detail: "System.Net.Http.HttpRequestException: 529\n   at ILD.Core.Adapters.ClaudeAdapter",
});

function mockHub() {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    connectionState: "connected",
    on: vi.fn((type: string, handler: Handler) => {
      const set = handlers.get(type) ?? new Set<Handler>();
      set.add(handler);
      handlers.set(type, set);
    }),
    off: vi.fn((type: string, handler: Handler) => {
      handlers.get(type)?.delete(handler);
    }),
    invoke: vi.fn(),
  } as any);
}

/** The level the backend reports it is running at, and what it started at. */
function mockLevel(level = "Information", startupLevel = "Information") {
  return vi
    .spyOn(authServices.loggingService, "getLevel")
    .mockResolvedValue({ level, startupLevel, isOverride: level !== startupLevel });
}

function mockEntries(entries: LogEntry[] = []) {
  return vi.spyOn(authServices.loggingService, "getEntries").mockResolvedValue(entries);
}

const status = (level: string, startupLevel: string) => ({
  level,
  startupLevel,
  isOverride: level !== startupLevel,
});

beforeEach(() => {
  handlers.clear();
  mockHub();
  mockLevel();
  mockEntries();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Logging settings level", () => {
  test("shows the level the backend reports it is running at", async () => {
    mockLevel("Warning", "Warning");
    render(<LoggingSettings />);

    await waitFor(() => expect(levelButton("Warning").getAttribute("aria-pressed")).toBe("true"));
    const group = within(screen.getByRole("group", { name: "Log level override" }));
    expect(group.getAllByRole("button").map((b) => b.textContent)).toEqual([
      "Debug",
      "Information",
      "Warning",
      "Error",
    ]);
    expect(screen.getByText(/following/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Reset" })).toBeNull();
  });

  test("presses nothing when the level cannot be read", async () => {
    vi.spyOn(authServices.loggingService, "getLevel").mockRejectedValue(new Error("down"));
    render(<LoggingSettings />);

    const group = within(screen.getByRole("group", { name: "Log level override" }));
    await waitFor(() =>
      expect(group.getAllByRole("button").every((b) => b.getAttribute("aria-pressed") === "false")),
    );
  });

  test("reports a level that differs from the startup one as an override", async () => {
    mockLevel("Debug", "Information");
    render(<LoggingSettings />);

    await waitFor(() => expect(levelButton("Debug").getAttribute("aria-pressed")).toBe("true"));
    expect(screen.getByText(/overriding/i)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reset" })).toBeTruthy();
  });

  test("Reset puts the level back to the startup one", async () => {
    mockLevel("Debug", "Information");
    const setLevel = vi
      .spyOn(authServices.loggingService, "setLevel")
      .mockResolvedValue(status("Information", "Information"));

    render(<LoggingSettings />);
    fireEvent.click(await screen.findByRole("button", { name: "Reset" }));

    await waitFor(() => expect(setLevel).toHaveBeenCalledWith("Information"));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Reset" })).toBeNull());
  });

  test("a level read that lands after a click does not undo the click", async () => {
    let resolveRead: (s: authServices.LogLevelStatus) => void = () => {};
    vi.spyOn(authServices.loggingService, "getLevel").mockReturnValue(
      new Promise((resolve) => {
        resolveRead = resolve;
      }),
    );
    vi.spyOn(authServices.loggingService, "setLevel").mockResolvedValue(
      status("Debug", "Information"),
    );

    render(<LoggingSettings />);
    fireEvent.click(levelButton("Debug"));
    resolveRead(status("Information", "Information"));

    await waitFor(() => expect(levelButton("Debug").getAttribute("aria-pressed")).toBe("true"));
    expect(levelButton("Information").getAttribute("aria-pressed")).toBe("false");
  });

  test("calls the API when the level changes", async () => {
    const setLevel = vi
      .spyOn(authServices.loggingService, "setLevel")
      .mockResolvedValue(status("Debug", "Information"));

    render(<LoggingSettings />);
    fireEvent.click(levelButton("Debug"));

    await waitFor(() => expect(setLevel).toHaveBeenCalledWith("Debug"));
    expect(levelButton("Debug").getAttribute("aria-pressed")).toBe("true");
  });

  test("goes back to the level the backend reported when the API refuses the change", async () => {
    mockLevel("Warning", "Warning");
    vi.spyOn(authServices.loggingService, "setLevel").mockRejectedValue(new Error("network error"));

    render(<LoggingSettings />);
    await waitFor(() => expect(levelButton("Warning").getAttribute("aria-pressed")).toBe("true"));
    fireEvent.click(levelButton("Debug"));

    await waitFor(() => expect(levelButton("Warning").getAttribute("aria-pressed")).toBe("true"));
    expect(levelButton("Debug").getAttribute("aria-pressed")).toBe("false");
    expect(screen.getByText(/failed to change the level/i)).toBeTruthy();
  });
});

describe("Logging settings log view", () => {
  test("shows what the backend has written", async () => {
    const getEntries = mockEntries([failure, warning]);
    render(<LoggingSettings />);

    expect(await screen.findByText(/provider returned 529/)).toBeTruthy();
    expect(screen.getByText(/GitHub rate limit/)).toBeTruthy();
    expect(screen.getAllByText("ILD.Core.Executors.AINodeExecutor").length).toBeGreaterThan(0);
    expect(getEntries).toHaveBeenCalledWith(
      expect.objectContaining({ take: 200, minimumLevel: undefined, search: undefined }),
    );
  });

  test("says so when the backend has written nothing, and when it cannot be read", async () => {
    render(<LoggingSettings />);
    expect(await screen.findByText(/nothing written yet/i)).toBeTruthy();

    cleanup();
    vi.spyOn(authServices.loggingService, "getEntries").mockRejectedValue(new Error("denied"));
    render(<LoggingSettings />);
    expect(await screen.findByText(/failed to read the log/i)).toBeTruthy();
  });

  test("a line arriving over the hub lands at the top of the log", async () => {
    mockEntries([warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);

    emit("LogEntryAppended", entry({ id: 9, message: "Reclaimed 3 worktrees" }));

    await waitFor(() => expect(screen.getByText(/Reclaimed 3 worktrees/)).toBeTruthy());
    const messages = screen
      .getAllByText(/Reclaimed 3 worktrees|GitHub rate limit/)
      .map((n) => n.textContent);
    expect(messages[0]).toMatch(/Reclaimed 3 worktrees/);
  });

  test("the minimum level is a query the backend answers, not a filter on what is held", async () => {
    const getEntries = mockEntries([failure, warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);

    fireEvent.click(
      within(screen.getByRole("group", { name: "Minimum level" })).getByRole("button", {
        name: "Warning",
      }),
    );

    await waitFor(() =>
      expect(getEntries).toHaveBeenCalledWith(
        expect.objectContaining({ take: 200, minimumLevel: "Warning" }),
      ),
    );
  });

  test("asking for Debug asks the backend for Verbose too", async () => {
    const getEntries = mockEntries([]);
    render(<LoggingSettings />);
    await screen.findByText(/nothing written yet/i);

    fireEvent.click(
      within(screen.getByRole("group", { name: "Minimum level" })).getByRole("button", {
        name: "Debug",
      }),
    );

    await waitFor(() =>
      expect(getEntries).toHaveBeenCalledWith(expect.objectContaining({ minimumLevel: "Verbose" })),
    );
  });

  test("the text filter searches the whole log, not the lines already on screen", async () => {
    const getEntries = mockEntries([failure, warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);

    fireEvent.change(screen.getByLabelText("Filter the log"), { target: { value: "rate limit" } });

    await waitFor(() =>
      expect(getEntries).toHaveBeenCalledWith(
        expect.objectContaining({ take: 200, search: "rate limit" }),
      ),
    );
  });

  test("resuming Follow live re-reads what was written while it was off", async () => {
    const getEntries = mockEntries([warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);
    expect(getEntries).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByLabelText("Follow the log live"));
    getEntries.mockResolvedValue([entry({ id: 9, message: "Written while paused" }), warning]);
    // Pausing freezes the list rather than re-reading it.
    expect(getEntries).toHaveBeenCalledTimes(1);
    expect(screen.queryByText(/Written while paused/)).toBeNull();

    fireEvent.click(screen.getByLabelText("Follow the log live"));

    expect(await screen.findByText(/Written while paused/)).toBeTruthy();
  });

  test("filtering while Follow live is off still asks the backend", async () => {
    const getEntries = mockEntries([failure, warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);
    fireEvent.click(screen.getByLabelText("Follow the log live"));

    fireEvent.click(
      within(screen.getByRole("group", { name: "Minimum level" })).getByRole("button", {
        name: "Error",
      }),
    );

    await waitFor(() =>
      expect(getEntries).toHaveBeenCalledWith(
        expect.objectContaining({ take: 200, minimumLevel: "Error" }),
      ),
    );
  });

  test("searching while Follow live is off reaches past the lines on screen", async () => {
    const getEntries = mockEntries([warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);
    fireEvent.click(screen.getByLabelText("Follow the log live"));

    getEntries.mockResolvedValue([entry({ id: 9, message: "Scrolled off long ago" })]);
    fireEvent.change(screen.getByLabelText("Filter the log"), { target: { value: "scrolled" } });

    expect(await screen.findByText(/Scrolled off long ago/)).toBeTruthy();
  });

  test("stops appending live lines when Follow live is switched off", async () => {
    render(<LoggingSettings />);
    await screen.findByText(/nothing written yet/i);

    fireEvent.click(screen.getByLabelText("Follow the log live"));
    emit("LogEntryAppended", entry({ id: 9, message: "Reclaimed 3 worktrees" }));

    expect(screen.queryByText(/Reclaimed 3 worktrees/)).toBeNull();
  });

  test("a level Serilog has and the page does not still reads as a line", async () => {
    mockEntries([entry({ id: 4, level: "Fatal", message: "The host is going down" })]);
    render(<LoggingSettings />);

    const line = (await screen.findByText(/The host is going down/)).closest("li")!;
    expect(within(line).getByText("ERR")).toBeTruthy();
  });

  test("filters by minimum level", async () => {
    mockEntries([failure, warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);

    fireEvent.click(
      within(screen.getByRole("group", { name: "Minimum level" })).getByRole("button", {
        name: "Error",
      }),
    );

    expect(screen.queryByText(/GitHub rate limit/)).toBeNull();
    expect(screen.getByText(/provider returned 529/)).toBeTruthy();
  });

  test("filters by text and reports when nothing matches", async () => {
    mockEntries([failure, warning]);
    render(<LoggingSettings />);
    await screen.findByText(/GitHub rate limit/);

    fireEvent.change(screen.getByLabelText("Filter the log"), { target: { value: "PrStatus" } });
    expect(screen.getByText(/GitHub rate limit/)).toBeTruthy();
    expect(screen.queryByText(/provider returned 529/)).toBeNull();

    fireEvent.change(screen.getByLabelText("Filter the log"), { target: { value: "zzz" } });
    expect(screen.getByText(/nothing matches that filter/i)).toBeTruthy();
  });

  test("expands a line that carries a stack trace", async () => {
    mockEntries([failure]);
    render(<LoggingSettings />);

    const line = (await screen.findByText(/provider returned 529/)).closest("button")!;
    expect(screen.queryByText(/HttpRequestException/)).toBeNull();

    fireEvent.click(line);
    expect(screen.getByText(/HttpRequestException/)).toBeTruthy();
  });

  test("opens the details of a line that carries no stack trace", async () => {
    mockEntries([warning]);
    render(<LoggingSettings />);

    const line = (await screen.findByText(/GitHub rate limit/)).closest("button")!;
    expect(screen.queryByText("Source")).toBeNull();

    fireEvent.click(line);
    const details = line.parentElement!.querySelector("dl")!;
    expect(within(details as HTMLElement).getByText("ILD.Core.Remote.PrStatusPoller")).toBeTruthy();
    expect(within(details as HTMLElement).getByText("Warning")).toBeTruthy();
  });

  test("a second click closes the details again", async () => {
    mockEntries([warning]);
    render(<LoggingSettings />);

    const line = (await screen.findByText(/GitHub rate limit/)).closest("button")!;
    fireEvent.click(line);
    expect(line.getAttribute("aria-expanded")).toBe("true");

    fireEvent.click(line);
    expect(line.getAttribute("aria-expanded")).toBe("false");
    expect(line.parentElement!.querySelector("dl")).toBeNull();
  });
});
