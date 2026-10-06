import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import IldSettings from "./IldSettings";
import * as authServices from "../../../services/auth";
import type { AiProvider } from "../../../types";

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

  test("reads the automatic throttle resume, including a value stored in another casing", async () => {
    // The backend validates with bool.TryParse, so "True" is on everywhere else
    // in the system.
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === authServices.SchedulerSettingKeys.ThrottleAutoResume ? "True" : "5",
    }));

    render(<IldSettings />);

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

    render(<IldSettings />);

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

    render(<IldSettings />);

    const wait = (await screen.findByLabelText(/wait between attempts/i)) as HTMLInputElement;
    fireEvent.change(wait, { target: { value: "90" } });
    fireEvent.click(wait.parentElement!.querySelector("button")!);
    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(
        authServices.SchedulerSettingKeys.ThrottleRetryDelayMinutes,
        "90",
      );
    });

    const attempts = screen.getByLabelText(/attempts before asking you/i) as HTMLInputElement;
    fireEvent.change(attempts, { target: { value: "0" } });
    fireEvent.click(attempts.parentElement!.querySelector("button")!);
    await screen.findByText("Must be an integer between 1 and 100.");

    fireEvent.change(attempts, { target: { value: "3" } });
    fireEvent.click(attempts.parentElement!.querySelector("button")!);
    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SchedulerSettingKeys.ThrottleMaxRetries, "3");
    });
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

describe("Chat titles", () => {
  const SmartTitles = "chat.smartTitles";
  const TitleProviderTag = "chat.titleProviderTag";

  const provider = (name: string, isDefault: boolean, tags: string[]) =>
    ({ id: name, name, type: "claude-code", isDefault, tags }) as unknown as AiProvider;

  function stored(values: Record<string, string>) {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: values[key] ?? "5",
    }));
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([
      provider("Main", true, []),
      provider("Fast", false, ["Fast", "Cheap"]),
    ]);
  }

  function card() {
    return screen.getByRole("heading", { name: "Chat titles" }).closest("section") as HTMLElement;
  }

  test("off on a fresh install keeps the provider tag shut; switching it on opens the tag, which saves", async () => {
    stored({ [SmartTitles]: "false", [TitleProviderTag]: "" });
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockImplementation(async (key: string, value: string) => ({ key, value }));

    render(<IldSettings />);

    const toggle = within(card()).getByRole("checkbox", {
      name: /smart session titles/i,
    }) as HTMLInputElement;
    const tag = within(card()).getByLabelText("Provider tag") as HTMLInputElement;
    const save = within(card()).getByRole("button", { name: "Save" }) as HTMLButtonElement;
    await within(card()).findByText("Runs on the default provider (Main)");
    expect(toggle.checked).toBe(false);
    expect(tag.disabled).toBe(true);
    expect(save.disabled).toBe(true);

    fireEvent.click(toggle);
    await waitFor(() => expect(put).toHaveBeenCalledWith(SmartTitles, "true"));
    await waitFor(() => expect(tag.disabled).toBe(false));

    // The loop node's own field: suggestions from the providers' tags, and the
    // provider the tag resolves to.
    const suggestions = document.getElementById(tag.getAttribute("list")!)!;
    expect(Array.from(suggestions.querySelectorAll("option"), (o) => o.value)).toEqual([
      "Cheap",
      "Fast",
    ]);
    fireEvent.change(tag, { target: { value: "Fast" } });
    expect(within(card()).getByText("Runs on Fast")).toBeTruthy();
    expect(save.disabled).toBe(false);
    fireEvent.click(save);
    await waitFor(() => expect(put).toHaveBeenCalledWith(TitleProviderTag, "Fast"));
  });

  test("shows what is stored, and switching it off shuts the tag again", async () => {
    stored({ [SmartTitles]: "true", [TitleProviderTag]: "Nobody" });
    vi.spyOn(authServices.settingsService, "put").mockImplementation(
      async (key: string, value: string) => ({ key, value }),
    );

    render(<IldSettings />);

    const toggle = within(card()).getByRole("checkbox", {
      name: /smart session titles/i,
    }) as HTMLInputElement;
    const tag = within(card()).getByLabelText("Provider tag") as HTMLInputElement;
    await waitFor(() => expect(toggle.checked).toBe(true));
    await waitFor(() => expect(tag.value).toBe("Nobody"));
    expect(tag.disabled).toBe(false);
    expect(
      within(card()).getByText("No provider has this tag — runs on the default provider (Main)"),
    ).toBeTruthy();

    fireEvent.click(toggle);
    await waitFor(() => expect(tag.disabled).toBe(true));
    expect(
      (within(card()).getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });
});
