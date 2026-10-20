import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import SchedulerChip from "./SchedulerChip";
import * as authServices from "../services/auth";
import * as signalRHook from "../hooks/useSignalR";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

/**
 * A work-item hub that, like the real one, sends SchedulerStateChanged only to a
 * connection that has joined the board's group.
 */
function mockHub() {
  const handlers: Record<string, ((msg: { payload: { isPaused: boolean } }) => void)[]> = {};
  const hub = { joined: false };
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn((type: string, handler: (msg: { payload: { isPaused: boolean } }) => void) => {
      (handlers[type] ??= []).push(handler);
    }),
    off: vi.fn(),
    invoke: vi.fn(async (method: string) => {
      if (method === "SubscribeToWorkItems") hub.joined = true;
    }),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  const announce = (isPaused: boolean) => {
    if (!hub.joined) return;
    for (const h of handlers["SchedulerStateChanged"] ?? []) h({ payload: { isPaused } });
  };
  return { announce };
}

describe("SchedulerChip", () => {
  test("says nothing until the server has said whether the scheduler runs", () => {
    mockHub();
    vi.spyOn(authServices.settingsService, "get").mockReturnValue(new Promise(() => {}));
    render(<SchedulerChip />);
    expect(screen.queryByRole("switch")).toBeNull();
  });

  test("reads Running, and a click pauses the scheduler", async () => {
    mockHub();
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: authServices.SchedulerSettingKeys.IsPaused,
      value: "false",
    });
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockResolvedValue({ key: authServices.SchedulerSettingKeys.IsPaused, value: "true" });
    render(<SchedulerChip />);

    const chip = await screen.findByRole("switch", { name: "Scheduler running" });
    expect(chip.getAttribute("aria-checked")).toBe("true");
    expect(chip.textContent).toContain("Running");

    fireEvent.click(chip);
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.IsPaused, "true"),
    );
    await waitFor(() => expect(chip.textContent).toContain("Paused"));
    expect(chip.getAttribute("aria-checked")).toBe("false");
  });

  test("follows a change made elsewhere, as the hub announces it", async () => {
    const { announce } = mockHub();
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: authServices.SchedulerSettingKeys.IsPaused,
      value: "false",
    });
    render(<SchedulerChip />);
    const chip = await screen.findByRole("switch", { name: "Scheduler running" });

    act(() => announce(true));

    await waitFor(() => expect(chip.textContent).toContain("Paused"));
  });

  test("a failed change says so and leaves the chip as it was", async () => {
    mockHub();
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: authServices.SchedulerSettingKeys.IsPaused,
      value: "false",
    });
    vi.spyOn(authServices.settingsService, "put").mockRejectedValue(new Error("database down"));
    render(<SchedulerChip />);
    const chip = await screen.findByRole("switch", { name: "Scheduler running" });

    fireEvent.click(chip);

    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(chip.textContent).toContain("Running");
  });

  test("a failed change's message can be dismissed from the keyboard", async () => {
    mockHub();
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: authServices.SchedulerSettingKeys.IsPaused,
      value: "false",
    });
    vi.spyOn(authServices.settingsService, "put").mockRejectedValue(new Error("database down"));
    render(<SchedulerChip />);
    fireEvent.click(await screen.findByRole("switch", { name: "Scheduler running" }));

    const dismiss = within(await screen.findByRole("alert")).getByRole("button", {
      name: "Dismiss",
    });
    dismiss.focus();
    expect(document.activeElement).toBe(dismiss);
    fireEvent.click(dismiss);
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
