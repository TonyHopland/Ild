import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import IldSettings from "./IldSettings";
import * as authServices from "../../../services/auth";
import { WorkItemHubProvider } from "../../../hooks/useWorkItemHub";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Ild settings", () => {
  test("each numeric field validates against its own range", async () => {
    // A stored retention other than 0, so entering 0 is a change to save.
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => {
      if (key !== authServices.SchedulerSettingKeys.RunRetentionDays) throw new Error("offline");
      return { key, value: "30" };
    });
    const put = vi.spyOn(authServices.settingsService, "put");

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    // 0 is the floor for run retention but below the scheduler's minimum of 1.
    const concurrent = (await screen.findByLabelText(/max concurrent/i)) as HTMLInputElement;
    fireEvent.change(concurrent, { target: { value: "0" } });
    fireEvent.blur(concurrent);
    await screen.findByText("Must be an integer between 1 and 1000.");

    const retention = (await screen.findByDisplayValue("30")) as HTMLInputElement;
    expect(retention).toBe(screen.getByLabelText(/keep finished runs for/i));
    fireEvent.change(retention, { target: { value: "0" } });
    fireEvent.blur(retention);

    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.RunRetentionDays, "0");
    });
  });

  test("run retention shows 14 when unread and no longer names 0 as when done", async () => {
    vi.spyOn(authServices.settingsService, "get").mockRejectedValue(new Error("offline"));

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    const retention = (await screen.findByLabelText(/keep finished runs for/i)) as HTMLInputElement;
    expect(retention.value).toBe("14");

    fireEvent.change(retention, { target: { value: "3651" } });
    fireEvent.blur(retention);
    const error = await screen.findByText(/^Must be an integer between 0 .* and 3650\.$/);
    expect(error.textContent).not.toContain("when done");
  });

  test("saves the AI step cap", async () => {
    const put = vi.spyOn(authServices.settingsService, "put");

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    const steps = (await screen.findByLabelText(/max ai steps/i)) as HTMLInputElement;
    fireEvent.change(steps, { target: { value: "0" } });
    fireEvent.blur(steps);
    await screen.findByText("Must be an integer between 1 and 1000.");

    fireEvent.change(steps, { target: { value: "40" } });
    fireEvent.blur(steps);
    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.MaxAiTraversals, "40");
    });
  });

  test("edits the global variable length limit within 1 to 8192, showing 4000 when unread", async () => {
    vi.spyOn(authServices.settingsService, "get").mockRejectedValue(new Error("offline"));
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockResolvedValue({ key: "globalVariables.maxLength", value: "2000" });

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    const limit = (await screen.findByLabelText(/global variable/i)) as HTMLInputElement;
    expect(limit.value).toBe("4000");

    fireEvent.change(limit, { target: { value: "8193" } });
    fireEvent.blur(limit);
    await screen.findByText("Must be an integer between 1 and 8192.");
    expect(put).not.toHaveBeenCalledWith("globalVariables.maxLength", "8193");

    fireEvent.change(limit, { target: { value: "2000" } });
    fireEvent.blur(limit);
    await waitFor(() => {
      expect(put).toHaveBeenCalledWith("globalVariables.maxLength", "2000");
    });
  });

  test("reads whether the scheduler is running (on means running) and writes it back", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === authServices.SchedulerSettingKeys.IsPaused ? "true" : "5",
    }));
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockResolvedValue({ key: authServices.SchedulerSettingKeys.IsPaused, value: "false" });

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    const toggle = screen.getByRole("checkbox", { name: "Scheduler running" });
    // Paused on the server, so the switch is off once it has been read.
    await waitFor(() => expect((toggle as HTMLInputElement).disabled).toBe(false));
    expect((toggle as HTMLInputElement).checked).toBe(false);

    fireEvent.click(toggle);
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.IsPaused, "false"),
    );
    await waitFor(() => expect((toggle as HTMLInputElement).checked).toBe(true));
  });

  test("reads the automatic throttle resume, including a value stored in another casing", async () => {
    // The backend validates with bool.TryParse, so "True" is on everywhere else
    // in the system.
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === authServices.SchedulerSettingKeys.ThrottleAutoResume ? "True" : "5",
    }));

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    const toggle = screen.getByRole("checkbox", { name: /resume throttled runs automatically/i });
    await waitFor(() => expect((toggle as HTMLInputElement).checked).toBe(true));
  });

  test("turns the automatic throttle resume on", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === authServices.SchedulerSettingKeys.ThrottleAutoResume ? "false" : "5",
    }));
    const put = vi.spyOn(authServices.settingsService, "put").mockResolvedValue({
      key: authServices.SchedulerSettingKeys.ThrottleAutoResume,
      value: "true",
    });

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    fireEvent.click(screen.getByRole("checkbox", { name: /resume throttled runs automatically/i }));

    await waitFor(() =>
      expect(put).toHaveBeenCalledWith(
        authServices.SchedulerSettingKeys.ThrottleAutoResume,
        "true",
      ),
    );
  });

  test("saves the wait between attempts and the attempt count", async () => {
    const put = vi.spyOn(authServices.settingsService, "put");

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    const wait = (await screen.findByLabelText(/wait between attempts/i)) as HTMLInputElement;
    fireEvent.change(wait, { target: { value: "90" } });
    fireEvent.blur(wait);
    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(
        authServices.SchedulerSettingKeys.ThrottleRetryDelayMinutes,
        "90",
      );
    });

    const attempts = screen.getByLabelText(/attempts before asking you/i) as HTMLInputElement;
    fireEvent.change(attempts, { target: { value: "0" } });
    fireEvent.blur(attempts);
    await screen.findByText("Must be an integer between 1 and 100.");

    fireEvent.change(attempts, { target: { value: "3" } });
    fireEvent.blur(attempts);
    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.ThrottleMaxRetries, "3");
    });
  });

  test("keeps the scheduler switch where it was when the save fails", async () => {
    vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
      key: authServices.SchedulerSettingKeys.IsPaused,
      value: "false",
    });
    vi.spyOn(authServices.settingsService, "put").mockRejectedValue(new Error("database down"));

    render(<IldSettings />, { wrapper: WorkItemHubProvider });

    const toggle = screen.getByRole("checkbox", { name: "Scheduler running" });
    await waitFor(() => expect((toggle as HTMLInputElement).checked).toBe(true));
    fireEvent.click(toggle);

    expect(await screen.findByText("database down")).toBeTruthy();
    expect((toggle as HTMLInputElement).checked).toBe(true);
  });
});
