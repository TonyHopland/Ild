import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import LoggingSettings from "./LoggingSettings";
import * as authServices from "../../../services/auth";

const levelButton = (name: string) =>
  within(screen.getByRole("group", { name: "Log level override" })).getByRole("button", { name });

/** The level the backend reports it is running at, and what it started at. */
function mockLevel(level = "Information", startupLevel = "Information") {
  return vi
    .spyOn(authServices.loggingService, "getLevel")
    .mockResolvedValue({ level, startupLevel, isOverride: level !== startupLevel });
}

const status = (level: string, startupLevel: string) => ({
  level,
  startupLevel,
  isOverride: level !== startupLevel,
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
    mockLevel();
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
  test("filters by minimum level", () => {
    render(<LoggingSettings />);

    expect(screen.getAllByText(/backing off to 120s/).length).toBeGreaterThan(0);
    fireEvent.click(
      within(screen.getByRole("group", { name: "Minimum level" })).getByRole("button", {
        name: "Error",
      }),
    );

    expect(screen.queryByText(/backing off to 120s/)).toBeNull();
    expect(screen.getAllByText(/provider returned 529/).length).toBeGreaterThan(0);
  });

  test("filters by text and reports when nothing matches", () => {
    render(<LoggingSettings />);

    fireEvent.change(screen.getByLabelText("Filter the log"), { target: { value: "worktrees" } });
    expect(screen.getAllByText(/Reclaimed 3 worktrees/).length).toBeGreaterThan(0);
    expect(screen.queryByText(/backing off to 120s/)).toBeNull();

    fireEvent.change(screen.getByLabelText("Filter the log"), { target: { value: "zzz" } });
    expect(screen.getByText(/nothing matches that filter/i)).toBeTruthy();
  });

  test("expands a line that carries a stack trace", () => {
    render(<LoggingSettings />);

    const line = screen.getAllByText(/provider returned 529/)[0].closest("button")!;
    expect(screen.queryByText(/HttpRequestException/)).toBeNull();

    fireEvent.click(line);
    expect(screen.getByText(/HttpRequestException/)).toBeTruthy();
  });

  test("opens the details of a line that carries no stack trace", () => {
    render(<LoggingSettings />);

    const line = screen.getAllByText(/Reclaimed 3 worktrees/)[0].closest("button")!;
    expect(screen.queryByText("Source")).toBeNull();

    fireEvent.click(line);
    const details = line.parentElement!.querySelector("dl")!;
    expect(
      within(details as HTMLElement).getByText("ILD.Core.WorktreeRetentionSweeper"),
    ).toBeTruthy();
    expect(within(details as HTMLElement).getByText("Information")).toBeTruthy();
  });

  test("a second click closes the details again", () => {
    render(<LoggingSettings />);

    const line = screen.getAllByText(/Reclaimed 3 worktrees/)[0].closest("button")!;
    fireEvent.click(line);
    expect(line.getAttribute("aria-expanded")).toBe("true");

    fireEvent.click(line);
    expect(line.getAttribute("aria-expanded")).toBe("false");
    expect(line.parentElement!.querySelector("dl")).toBeNull();
  });
});
