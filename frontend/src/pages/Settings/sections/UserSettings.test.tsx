import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import UserSettings from "./UserSettings";
import * as useAuthHook from "../../../hooks/useAuth";
import * as authServices from "../../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

beforeEach(() => {
  localStorage.clear();
  vi.spyOn(useAuthHook, "useAuth").mockReturnValue({
    user: { id: "1", username: "testuser", createdAt: "2025-01-01" },
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  } as any);
});

describe("User settings signed-in devices", () => {
  const thisDevice = {
    id: "s1",
    createdAt: "2026-08-01T10:00:00Z",
    lastSeenAt: "2026-08-07T10:00:00Z",
    userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) Chrome/120.0",
    createdFromIp: "10.0.0.2",
    isCurrent: true,
  };
  const otherDevice = {
    id: "s2",
    createdAt: "2026-08-02T10:00:00Z",
    lastSeenAt: "2026-08-06T10:00:00Z",
    userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0) Safari/605.1",
    createdFromIp: "10.0.0.3",
    isCurrent: false,
  };

  test("lists each device and marks the current one", async () => {
    vi.spyOn(authServices.authService, "getSessions").mockResolvedValue([thisDevice, otherDevice]);

    render(<UserSettings />);

    expect(await screen.findByText("Chrome on macOS")).toBeTruthy();
    expect(screen.getByText("Safari on iPhone")).toBeTruthy();
    expect(screen.getByText("This device")).toBeTruthy();
  });

  test("offers no sign-out button for the current device", async () => {
    vi.spyOn(authServices.authService, "getSessions").mockResolvedValue([thisDevice, otherDevice]);

    render(<UserSettings />);

    await screen.findByText("Chrome on macOS");
    expect(screen.queryByRole("button", { name: /sign out chrome on macos/i })).toBeNull();
    expect(screen.getByRole("button", { name: /sign out safari on iphone/i })).toBeTruthy();
  });

  test("revokes a device and reloads the list", async () => {
    const getSessions = vi
      .spyOn(authServices.authService, "getSessions")
      .mockResolvedValueOnce([thisDevice, otherDevice])
      .mockResolvedValueOnce([thisDevice]);
    const revoke = vi.spyOn(authServices.authService, "revokeSession").mockResolvedValue(undefined);

    render(<UserSettings />);

    fireEvent.click(await screen.findByRole("button", { name: /sign out safari on iphone/i }));

    await waitFor(() => {
      expect(revoke).toHaveBeenCalledWith("s2");
      expect(getSessions).toHaveBeenCalledTimes(2);
    });
    await waitFor(() => {
      expect(screen.queryByText("Safari on iPhone")).toBeNull();
    });
  });

  test("disables sign out everywhere else when this is the only device", async () => {
    vi.spyOn(authServices.authService, "getSessions").mockResolvedValue([thisDevice]);

    render(<UserSettings />);

    await screen.findByText("Chrome on macOS");
    const button = screen.getByRole("button", { name: /sign out everywhere else/i });
    expect((button as HTMLButtonElement).disabled).toBe(true);
  });

  test("signs out everywhere else", async () => {
    vi.spyOn(authServices.authService, "getSessions")
      .mockResolvedValueOnce([thisDevice, otherDevice])
      .mockResolvedValueOnce([thisDevice]);
    const revokeOthers = vi
      .spyOn(authServices.authService, "revokeOtherSessions")
      .mockResolvedValue(1);

    render(<UserSettings />);

    await screen.findByText("Safari on iPhone");
    fireEvent.click(screen.getByRole("button", { name: /sign out everywhere else/i }));

    await waitFor(() => {
      expect(revokeOthers).toHaveBeenCalled();
    });
  });

  test("saves the idle expiry setting", async () => {
    vi.spyOn(authServices.authService, "getSessions").mockResolvedValue([thisDevice]);
    vi.spyOn(authServices.settingsService, "get").mockImplementation(async (key: string) => ({
      key,
      value: key === authServices.SessionSettingKeys.IdleDays ? "30" : "90",
    }));
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockResolvedValue({ key: authServices.SessionSettingKeys.IdleDays, value: "7" });

    render(<UserSettings />);

    const input = (await screen.findByLabelText(/sign out after inactivity/i)) as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe("30"));
    fireEvent.change(input, { target: { value: "7" } });
    fireEvent.click(input.parentElement!.querySelector("button")!);

    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SessionSettingKeys.IdleDays, "7");
    });
  });

  test("saves the absolute expiry setting independently of the idle one", async () => {
    vi.spyOn(authServices.authService, "getSessions").mockResolvedValue([thisDevice]);
    const put = vi
      .spyOn(authServices.settingsService, "put")
      .mockResolvedValue({ key: authServices.SessionSettingKeys.MaxDays, value: "180" });

    render(<UserSettings />);

    const input = (await screen.findByLabelText(/however active/i)) as HTMLInputElement;
    fireEvent.change(input, { target: { value: "180" } });
    fireEvent.click(input.parentElement!.querySelector("button")!);

    await waitFor(() => {
      expect(put).toHaveBeenCalledWith(authServices.SessionSettingKeys.MaxDays, "180");
    });
    expect(put).toHaveBeenCalledTimes(1);
  });

  test("rejects an out-of-range expiry without calling the API", async () => {
    vi.spyOn(authServices.authService, "getSessions").mockResolvedValue([thisDevice]);
    const put = vi.spyOn(authServices.settingsService, "put");

    render(<UserSettings />);

    const input = (await screen.findByLabelText(/sign out after inactivity/i)) as HTMLInputElement;
    fireEvent.change(input, { target: { value: "-1" } });
    fireEvent.click(input.parentElement!.querySelector("button")!);

    await screen.findByText(/must be an integer between 0/i);
    expect(put).not.toHaveBeenCalled();
  });
});

describe("User settings preferences", () => {
  test("shows notification toggle enabled by default", () => {
    render(<UserSettings />);

    const toggle = screen.getByRole("checkbox", { name: /browser notifications/i });
    expect((toggle as HTMLInputElement).checked).toBe(true);
  });

  test("persists notification preference to localStorage when toggled off", () => {
    render(<UserSettings />);

    fireEvent.click(screen.getByRole("checkbox", { name: /browser notifications/i }));
    expect(localStorage.getItem("ild_notifications_enabled")).toBe("false");
  });

  test("reads notification preference from localStorage on mount", () => {
    localStorage.setItem("ild_notifications_enabled", "false");

    render(<UserSettings />);

    const toggle = screen.getByRole("checkbox", { name: /browser notifications/i });
    expect((toggle as HTMLInputElement).checked).toBe(false);
  });

  test("shows the chat toggle enabled by default", () => {
    render(<UserSettings />);

    const toggle = screen.getByRole("checkbox", { name: /enable ai chat bubble/i });
    expect((toggle as HTMLInputElement).checked).toBe(true);
  });

  test("persists the chat preference to localStorage when toggled off", () => {
    render(<UserSettings />);

    const toggle = screen.getByRole("checkbox", { name: /enable ai chat bubble/i });
    fireEvent.click(toggle);
    expect(localStorage.getItem("ild_chat_enabled")).toBe("false");
    expect((toggle as HTMLInputElement).checked).toBe(false);
  });

  test("reads a disabled chat preference from localStorage on mount", () => {
    localStorage.setItem("ild_chat_enabled", "false");

    render(<UserSettings />);

    const toggle = screen.getByRole("checkbox", { name: /enable ai chat bubble/i });
    expect((toggle as HTMLInputElement).checked).toBe(false);
  });
});
