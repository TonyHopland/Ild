import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../hooks/useAuth";
import Header from "./Header";
import { WorkItemHubProvider } from "../hooks/useWorkItemHub";

afterEach(cleanup);

function renderHeader() {
  const authValue = {
    user: { id: "1", username: "test", createdAt: "" },
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  };

  render(
    <MemoryRouter initialEntries={["/taskboard"]}>
      <AuthContext.Provider value={authValue}>
        <Header />
      </AuthContext.Provider>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
}

describe("Header nav items", () => {
  test("has no WorkItem Server, Loop Runs or Remote Providers link and keeps every other item in order", () => {
    renderHeader();

    const nav = within(screen.getByRole("navigation"));
    expect(nav.getAllByRole("link").map((l) => l.textContent)).toEqual([
      "Taskboard",
      "Loop Editor",
      "Analytics",
      "Chat",
      "Settings",
      "Repositories",
      "AI Providers",
      "Global Variables",
    ]);
    expect(nav.getByRole("link", { name: "Global Variables" }).getAttribute("href")).toBe(
      "/global-variables",
    );
  });
});

describe("Header brand", () => {
  test("shows the logo inside the brand link, which keeps the name ILD", () => {
    renderHeader();

    const brand = screen.getByRole("link", { name: "ILD" });
    const logo = brand.querySelector("img");
    expect(logo?.getAttribute("src")).toBe("/ild.svg");
    expect(logo?.getAttribute("alt")).toBe("");
  });
});
