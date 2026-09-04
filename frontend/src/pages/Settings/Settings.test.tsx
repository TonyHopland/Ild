import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import Settings from "./index";
import * as useAuthHook from "../../hooks/useAuth";
import * as signalRHook from "../../hooks/useSignalR";

function renderAt(path: string) {
  vi.spyOn(useAuthHook, "useAuth").mockReturnValue({
    user: { id: "1", username: "testuser", createdAt: "2025-01-01" },
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  } as any);
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    connectionState: "connected",
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(),
  } as any);

  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/settings" element={<Settings />} />
        <Route path="/settings/:section" element={<Settings />} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Settings sections", () => {
  test("links every section from the side menu", () => {
    renderAt("/settings");

    const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
    expect(nav.getAllByRole("link").map((l) => l.getAttribute("href"))).toEqual([
      "/settings/ild",
      "/settings/user",
      "/settings/network",
      "/settings/logging",
    ]);
  });

  test("opens on Ild when no section is named, and says so in the menu", () => {
    renderAt("/settings");

    expect(screen.getByRole("heading", { level: 2, name: "Ild" })).toBeTruthy();
    expect(screen.getByLabelText(/max concurrent/i)).toBeTruthy();
    // The URL names no section, so nothing but the page itself knows Ild is the
    // one on screen: what is highlighted has to be announced as current too.
    const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
    expect(nav.getByRole("link", { name: "Ild" }).getAttribute("aria-current")).toBe("page");
    expect(nav.getByRole("link", { name: "Logging" }).getAttribute("aria-current")).toBeNull();
  });

  test("shows the section named in the URL and marks it in the menu", () => {
    renderAt("/settings/logging");

    expect(screen.getByRole("heading", { level: 2, name: "Logging" })).toBeTruthy();
    const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
    expect(nav.getByRole("link", { name: "Logging" }).className).toContain("active");
    expect(nav.getByRole("link", { name: "Logging" }).getAttribute("aria-current")).toBe("page");
    expect(nav.getByRole("link", { name: "Ild" }).className).not.toContain("active");
    expect(nav.getByRole("link", { name: "Ild" }).getAttribute("aria-current")).toBeNull();
  });

  test("falls back to the first section for an unknown one", () => {
    renderAt("/settings/nonsense");

    expect(screen.getByRole("heading", { level: 2, name: "Ild" })).toBeTruthy();
    const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
    expect(nav.getByRole("link", { name: "Ild" }).getAttribute("aria-current")).toBe("page");
  });
});
