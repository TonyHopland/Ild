import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor, within } from "@testing-library/react";
import IldSettings from "./IldSettings";
import * as authServices from "../../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Delete inactive chats", () => {
  test("is off on the Ild page with the days at 14, and once on saves the days within 1 to 3650", async () => {
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
    await waitFor(() =>
      expect(authServices.settingsService.get).toHaveBeenCalledWith("chat.autoDeleteInactiveDays"),
    );
    expect(toggle.checked).toBe(false);
    expect(days.value).toBe("14");

    fireEvent.click(toggle);
    await waitFor(() => expect(put).toHaveBeenCalledWith("chat.autoDeleteInactive", "true"));
    expect(toggle.checked).toBe(true);

    for (const outOfRange of ["0", "3651"]) {
      fireEvent.change(days, { target: { value: outOfRange } });
      // The field saves as it is left.
      fireEvent.blur(days);
      await card.findByText("Must be an integer between 1 and 3650.");
      expect(put).not.toHaveBeenCalledWith("chat.autoDeleteInactiveDays", outOfRange);
    }

    fireEvent.change(days, { target: { value: "30" } });
    fireEvent.blur(days);
    await waitFor(() => expect(put).toHaveBeenCalledWith("chat.autoDeleteInactiveDays", "30"));
    expect(await card.findByText("Saved")).toBeTruthy();
  });
});
