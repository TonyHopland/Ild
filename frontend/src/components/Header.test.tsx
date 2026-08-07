import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../hooks/useAuth";
import { NAV_ITEMS } from "../utils/constants";
import Header from "./Header";

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
  );
}

// The header CSS lives in an inline <style> block, which jsdom does apply, so
// the layout rules that keep every nav item reachable on a narrow viewport are
// assertable through getComputedStyle. jsdom does no real layout, so this pins
// the rules rather than an observed overflow.
describe("Header nav overflow", () => {
  test("nav scrolls sideways instead of growing past the viewport", () => {
    renderHeader();

    const nav = screen.getByRole("navigation");
    const style = getComputedStyle(nav);

    expect(style.overflowX).toBe("auto");
    // Without this the nav refuses to shrink below its content width and the
    // trailing items are pushed off-screen with nothing to scroll.
    expect(style.minWidth).toBe("0px");
  });

  test("nav items keep their size and stay on one line", () => {
    renderHeader();

    for (const item of NAV_ITEMS) {
      const style = getComputedStyle(screen.getByRole("link", { name: item.label }));
      expect(style.flexShrink).toBe("0");
      expect(style.whiteSpace).toBe("nowrap");
    }
  });

  test("username and logout button are not squeezed by the nav", () => {
    renderHeader();

    expect(screen.getByText("test")).toBeTruthy();
    const user = screen.getByRole("button", { name: "Logout" }).parentElement!;
    expect(getComputedStyle(user).flexShrink).toBe("0");
  });
});
