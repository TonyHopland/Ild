import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, within, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import Settings from "./index";
import { globalVariablesRedirect } from "../../utils/legacyRoutes";
import * as useAuthHook from "../../hooks/useAuth";
import * as signalRHook from "../../hooks/useSignalR";
import * as pendingHook from "../../hooks/usePendingGlobalVariableProposalCount";
import * as services from "../../services/auth";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

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
    { wrapper: WorkItemHubProvider },
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
      "/settings/global-variables",
      "/settings/user",
      "/settings/network",
      "/settings/package-feeds",
      "/settings/logging",
      "/settings/work-item-server",
      "/settings/storage",
      "/settings/ai-access",
    ]);
  });

  test("opens on Ild when no section is named, and says so in the menu", () => {
    renderAt("/settings");

    expect(screen.getByRole("heading", { level: 2, name: "General" })).toBeTruthy();
    expect(screen.getByLabelText(/max concurrent/i)).toBeTruthy();
    // The URL names no section, so nothing but the page itself knows Ild is the
    // one on screen: what is highlighted has to be announced as current too.
    const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
    expect(nav.getByRole("link", { name: "General" }).getAttribute("aria-current")).toBe("page");
    expect(nav.getByRole("link", { name: "Logging" }).getAttribute("aria-current")).toBeNull();
  });

  test("shows the section named in the URL and marks it in the menu", () => {
    renderAt("/settings/logging");

    expect(screen.getByRole("heading", { level: 2, name: "Logging" })).toBeTruthy();
    const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
    expect(nav.getByRole("link", { name: "Logging" }).className).toContain("active");
    expect(nav.getByRole("link", { name: "Logging" }).getAttribute("aria-current")).toBe("page");
    expect(nav.getByRole("link", { name: "General" }).className).not.toContain("active");
    expect(nav.getByRole("link", { name: "General" }).getAttribute("aria-current")).toBeNull();
  });

  test("falls back to the first section for an unknown one", () => {
    renderAt("/settings/nonsense");

    expect(screen.getByRole("heading", { level: 2, name: "General" })).toBeTruthy();
    const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
    expect(nav.getByRole("link", { name: "General" }).getAttribute("aria-current")).toBe("page");
  });

  test.each([
    [
      "WorkItem Server",
      "work-item-server",
      () =>
        vi.spyOn(services.workItemServerService, "get").mockResolvedValue({
          url: null,
          hasApiKey: false,
          pollIntervalSeconds: 60,
          graceIntervalSeconds: 5,
        }),
      async () => {
        await screen.findByLabelText(/url/i);
      },
    ],
    [
      "Storage",
      "storage",
      () =>
        vi.spyOn(services.storageService, "getOverview").mockResolvedValue({
          categories: [],
          databases: [],
        }),
      async (loaded: ReturnType<typeof vi.spyOn>) => {
        await waitFor(() => expect(loaded).toHaveBeenCalled());
      },
    ],
  ] as const)(
    "shows the %s section at its own URL and marks only it current",
    async (name, path, mockService, loaded) => {
      const spy = mockService();
      renderAt(`/settings/${path}`);

      expect(screen.getByRole("heading", { level: 2, name })).toBeTruthy();
      const nav = within(screen.getByRole("navigation", { name: /settings sections/i }));
      const link = nav.getByRole("link", { name });
      expect(link.className).toContain("active");
      expect(
        nav.getAllByRole("link").filter((l) => l.getAttribute("aria-current") === "page"),
      ).toEqual([link]);
      await loaded(spy as never);
    },
  );
});

function Where() {
  return <div data-testid="where">{useLocation().pathname}</div>;
}

describe("the old Global Variables address", () => {
  test("redirects /global-variables to its Settings section", () => {
    render(
      <MemoryRouter initialEntries={["/global-variables"]}>
        <Routes>
          {globalVariablesRedirect}
          <Route path="/settings/:section" element={<Where />} />
        </Routes>
      </MemoryRouter>,
    );
    expect(screen.getByTestId("where").textContent).toBe("/settings/global-variables");
  });
});

describe("the pending suggestions badge in the Settings menu", () => {
  const pending = (count: number) =>
    vi.spyOn(pendingHook, "usePendingGlobalVariableProposalCount").mockReturnValue(count);
  const link = () =>
    within(screen.getByRole("navigation", { name: /settings sections/i })).getByRole("link", {
      name: /Global variables/,
    });

  test("shows the count on Global variables, and nothing at zero", () => {
    pending(3);
    renderAt("/settings/ild");
    expect(link().textContent).toContain("3");
    expect(link().textContent).toContain("suggestions waiting");
    cleanup();

    pending(0);
    renderAt("/settings/ild");
    expect(link().textContent).toBe("Global variables");
  });
});
