import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import IldSettings from "./IldSettings";
import * as authServices from "../../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Ild settings", () => {
  test("each numeric field validates against its own range", async () => {
    const put = vi.spyOn(authServices.settingsService, "put");

    render(<IldSettings />);

    // 0 is the floor for run retention but below the scheduler's minimum of 1.
    const concurrent = (await screen.findByLabelText(/max concurrent/i)) as HTMLInputElement;
    fireEvent.change(concurrent, { target: { value: "0" } });
    fireEvent.click(concurrent.parentElement!.querySelector("button")!);
    await screen.findByText("Must be an integer between 1 and 1000.");

    const retention = screen.getByLabelText(/delete finished runs after/i) as HTMLInputElement;
    fireEvent.change(retention, { target: { value: "0" } });
    fireEvent.click(retention.parentElement!.querySelector("button")!);

    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.RunRetentionDays, "0");
    });
  });

  test("saves the AI step cap", async () => {
    const put = vi.spyOn(authServices.settingsService, "put");

    render(<IldSettings />);

    const steps = (await screen.findByLabelText(/max ai steps/i)) as HTMLInputElement;
    fireEvent.change(steps, { target: { value: "0" } });
    fireEvent.click(steps.parentElement!.querySelector("button")!);
    await screen.findByText("Must be an integer between 1 and 1000.");

    fireEvent.change(steps, { target: { value: "40" } });
    fireEvent.click(steps.parentElement!.querySelector("button")!);
    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.MaxAiTraversals, "40");
    });
  });

  test("reads the scheduler pause and writes it back", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === authServices.SchedulerSettingKeys.IsPaused ? "true" : "5",
    }));
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockResolvedValue({ key: authServices.SchedulerSettingKeys.IsPaused, value: "false" });

    render(<IldSettings />);

    const toggle = screen.getByRole("checkbox", { name: /pause the scheduler/i });
    await waitFor(() => expect((toggle as HTMLInputElement).checked).toBe(true));

    fireEvent.click(toggle);
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.IsPaused, "false"),
    );
  });

  test("puts the pause back when the save fails", async () => {
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: authServices.SchedulerSettingKeys.IsPaused,
      value: "false",
    });
    vi.spyOn(authServices.settingsService, "put").mockRejectedValue(new Error("database down"));

    render(<IldSettings />);

    const toggle = screen.getByRole("checkbox", { name: /pause the scheduler/i });
    fireEvent.click(toggle);

    expect(await screen.findByText("database down")).toBeTruthy();
    expect((toggle as HTMLInputElement).checked).toBe(false);
  });
});
