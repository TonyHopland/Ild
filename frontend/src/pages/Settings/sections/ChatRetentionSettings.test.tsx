import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import IldSettings from "./IldSettings";
import * as authServices from "../../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Delete inactive chats", () => {
  test("is off on the Ild page with the days shut at 14, and once on saves the days within 1 to 3650", async () => {
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => {
      if (key === "chat.autoDeleteInactive") return { key, value: "false" };
      throw new Error("offline");
    });
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockImplementation(async (key: string, value: string) => ({ key, value }));

    render(<IldSettings />);

    const toggle = (await screen.findByRole("checkbox", {
      name: /delete inactive chats/i,
    })) as HTMLInputElement;
    const card = within(toggle.closest(".settings-card")!);
    const days = card.getByRole("spinbutton") as HTMLInputElement;
    const saveDays = days.parentElement!.querySelector("button")!;
    await waitFor(() =>
      expect(authServices.settingsService.get).toHaveBeenCalledWith("chat.autoDeleteInactiveDays"),
    );
    expect(toggle.checked).toBe(false);
    expect(days.value).toBe("14");
    expect(days.disabled).toBe(true);

    fireEvent.click(toggle);
    await waitFor(() => expect(put).toHaveBeenCalledWith("chat.autoDeleteInactive", "true"));
    await waitFor(() => expect(days.disabled).toBe(false));
    expect(toggle.checked).toBe(true);

    for (const outOfRange of ["0", "3651"]) {
      fireEvent.change(days, { target: { value: outOfRange } });
      fireEvent.click(saveDays);
      await card.findByText("Must be an integer between 1 and 3650.");
      expect(put).not.toHaveBeenCalledWith("chat.autoDeleteInactiveDays", outOfRange);
    }

    fireEvent.change(days, { target: { value: "30" } });
    fireEvent.click(saveDays);
    await waitFor(() => expect(put).toHaveBeenCalledWith("chat.autoDeleteInactiveDays", "30"));
  });
});
