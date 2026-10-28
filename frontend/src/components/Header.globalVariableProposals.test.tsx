import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, within, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { AuthContext } from "../hooks/useAuth";
import * as signalRHook from "../hooks/useSignalR";
import Header from "./Header";
import { fakeHub, reply, stubFetch, routeEveryHub } from "../test-support";
import { WorkItemHubProvider } from "../hooks/useWorkItemHub";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function mockHub() {
  const hub = fakeHub();
  routeEveryHub(signalRHook, hub);
  return hub.emit;
}

/** Serves the pending-suggestion count the server holds now; every other request fails. */
function stubCount(server: { count: number | null }) {
  stubFetch((call) =>
    call.path === "/api/v1/global-variables/proposals/pending-count" && server.count !== null
      ? reply(200, { count: server.count })
      : reply(404, { error: "not stubbed" }),
  );
}

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

const globalVariablesLink = () =>
  within(screen.getByRole("navigation")).getByRole("link", { name: /Global Variables/ });

describe("the Global Variables menu entry", () => {
  test("carries the number of pending suggestions, follows hints, and shows nothing at zero", async () => {
    const hint = mockHub();
    const server = { count: 3 as number | null };
    stubCount(server);

    renderHeader();

    await waitFor(() => expect(globalVariablesLink().textContent).toMatch(/\b3\b/));

    server.count = 4;
    await hint("GlobalVariableProposalsChanged", { name: "review_rules" });
    await waitFor(() => expect(globalVariablesLink().textContent).toMatch(/\b4\b/));

    server.count = 0;
    await hint("GlobalVariableProposalsChanged", { name: "review_rules" });
    await waitFor(() => expect(globalVariablesLink().textContent).toBe("Global Variables"));
  });
});
