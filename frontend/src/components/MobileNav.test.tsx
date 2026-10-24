import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, within, waitFor, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../hooks/useAuth";
import * as signalRHook from "../hooks/useSignalR";
import MobileTabBar from "./MobileNav";
import { fakeHub, reply, stubFetch } from "../test-support";
import { WorkItemHubProvider } from "../hooks/useWorkItemHub";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function mockHub() {
  const hub = fakeHub();
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue(hub.connection);
  return hub.emit;
}

/** Serves the board counts the server holds now; every other request fails. */
function stubCounts(server: { needsYou: number | null }) {
  stubFetch((call) =>
    call.path === "/api/v1/workitems/counts" && server.needsYou !== null
      ? reply(200, { HumanFeedback: server.needsYou, Running: 2 })
      : reply(404, { error: "not stubbed" }),
  );
}

function renderTabBar(path = "/taskboard") {
  const logout = vi.fn().mockResolvedValue(undefined);
  render(
    <MemoryRouter initialEntries={[path]}>
      <AuthContext.Provider
        value={{
          user: { id: "1", username: "tester", createdAt: "" },
          token: "t",
          isAuthenticated: true,
          isLoading: false,
          login: vi.fn(),
          logout,
        }}
      >
        <MobileTabBar />
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
  return { logout };
}

const tabBar = () => within(screen.getByRole("navigation", { name: "Main" }));

describe("the phone tab bar", () => {
  test("offers the four main pages and More, marking the one open", () => {
    mockHub();
    stubCounts({ needsYou: null });
    renderTabBar("/analytics");

    expect(
      tabBar()
        .getAllByRole("link")
        .map((l) => l.textContent),
    ).toEqual(["Taskboard", "Loops", "Analytics", "Chat"]);
    expect(tabBar().getByRole("link", { name: "Analytics" }).className).toContain("is-active");
    expect(tabBar().getByRole("button", { name: "More" })).toBeTruthy();
  });

  test("Taskboard carries how many items need you, follows status changes, and shows nothing at zero", async () => {
    const emit = mockHub();
    const server = { needsYou: 3 as number | null };
    stubCounts(server);
    renderTabBar();

    const taskboard = () => tabBar().getByRole("link", { name: /Taskboard/ });
    await waitFor(() => expect(taskboard().textContent).toBe("Taskboard3 need you"));

    server.needsYou = 0;
    await emit("WorkItemStateChanged", { workItemId: "w1", oldStatus: "x", newStatus: "y" });
    await waitFor(() => expect(taskboard().textContent).toBe("Taskboard"));
  });

  test("shows no count when the counts cannot be read", async () => {
    mockHub();
    stubCounts({ needsYou: null });
    // The failed read is logged as it settles: once it has been, the badge has had its answer.
    const logged = vi.spyOn(console, "error").mockImplementation(() => {});
    renderTabBar();

    await waitFor(() => expect(logged).toHaveBeenCalled());
    expect(tabBar().getByRole("link", { name: /Taskboard/ }).textContent).toBe("Taskboard");
  });

  test("More opens the menu with the other pages and Log out, and Escape closes it", async () => {
    mockHub();
    stubCounts({ needsYou: null });
    const { logout } = renderTabBar("/repositories");

    const more = tabBar().getByRole("button", { name: "More" });
    // On one of the menu's pages, More is the tab marked as open.
    expect(more.className).toContain("is-active");
    fireEvent.click(more);

    const menu = within(screen.getByRole("dialog", { name: "Menu" }));
    expect(menu.getAllByRole("link").map((l) => l.textContent)).toEqual([
      "Settings",
      "Repositories",
      "AI Providers",
      "Global Variables",
    ]);
    expect(menu.getByText("tester")).toBeTruthy();

    fireEvent.keyDown(window, { key: "Escape" });
    expect(screen.queryByRole("dialog", { name: "Menu" })).toBeNull();

    fireEvent.click(more);
    fireEvent.click(screen.getByRole("button", { name: "Log out" }));
    expect(logout).toHaveBeenCalled();
    expect(screen.queryByRole("dialog", { name: "Menu" })).toBeNull();
  });
});
