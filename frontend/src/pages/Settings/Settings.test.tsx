import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Settings from "./index";
import * as useAuthHook from "../../hooks/useAuth";
import * as authServices from "../../services/auth";

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

describe("Settings notifications", () => {
  test("shows notification toggle enabled by default", () => {
    render(
      <MemoryRouter>
        <Settings />
      </MemoryRouter>,
    );

    const toggle = screen.getByRole("checkbox", { name: /browser notifications/i });
    expect((toggle as HTMLInputElement).checked).toBe(true);
  });

  test("persists notification preference to localStorage when toggled off", () => {
    render(
      <MemoryRouter>
        <Settings />
      </MemoryRouter>,
    );

    const toggle = screen.getByRole("checkbox", { name: /browser notifications/i });
    fireEvent.click(toggle);
    expect(localStorage.getItem("ild_notifications_enabled")).toBe("false");
  });

  test("reads notification preference from localStorage on mount", () => {
    localStorage.setItem("ild_notifications_enabled", "false");

    render(
      <MemoryRouter>
        <Settings />
      </MemoryRouter>,
    );

    const toggle = screen.getByRole("checkbox", { name: /browser notifications/i });
    expect((toggle as HTMLInputElement).checked).toBe(false);
  });
});

describe("Settings log level", () => {
  test("renders log level dropdown with all options", () => {
    vi.spyOn(authServices.loggingService, "setLevel").mockResolvedValue({ level: "Debug" });

    render(
      <MemoryRouter>
        <Settings />
      </MemoryRouter>,
    );

    const select = screen.getByRole("combobox", { name: /backend log level/i });
    expect(select).toBeTruthy();

    const options = screen.getAllByRole("option");
    expect(options).toHaveLength(4);
    expect(options.map((o) => o.textContent)).toEqual(["Debug", "Information", "Warning", "Error"]);
  });

  test("calls API when log level is changed", async () => {
    const setLevelMock = vi
      .spyOn(authServices.loggingService, "setLevel")
      .mockResolvedValue({ level: "Debug" });

    render(
      <MemoryRouter>
        <Settings />
      </MemoryRouter>,
    );

    const select = screen.getByRole("combobox", {
      name: /backend log level/i,
    }) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "Debug" } });

    await waitFor(() => {
      expect(setLevelMock).toHaveBeenCalledWith("Debug");
    });
  });

  test("reverts selection on API failure", async () => {
    vi.spyOn(authServices.loggingService, "setLevel").mockRejectedValue(new Error("network error"));

    render(
      <MemoryRouter>
        <Settings />
      </MemoryRouter>,
    );

    const select = screen.getByRole("combobox", {
      name: /backend log level/i,
    }) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "Debug" } });

    await waitFor(() => {
      expect((select as HTMLSelectElement).value).toBe("Information");
    });
  });
});
