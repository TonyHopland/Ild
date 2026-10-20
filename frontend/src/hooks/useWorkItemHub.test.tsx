import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { cleanup, render } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import * as signalRHook from "./useSignalR";
import { WorkItemHubProvider } from "./useWorkItemHub";
import { fakeHub, reply, stubFetch } from "../test-support";
import { AuthContext } from "./useAuth";
import SchedulerChip from "../components/SchedulerChip";
import MobileTabBar from "../components/MobileNav";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("the shared work-item hub", () => {
  test("the scheduler chip and the tab bar's count listen on the one connection the provider opens", () => {
    const shared = fakeHub();
    const unused = fakeHub();
    // The provider asks for the hub by URL; a hook handed a connection asks for none.
    const useSignalR = vi
      .spyOn(signalRHook, "useSignalR")
      .mockImplementation((url) => (url === null ? unused.connection : shared.connection));
    stubFetch(() => reply(404, { error: "not stubbed" }));

    render(
      <MemoryRouter>
        <AuthContext.Provider
          value={{
            user: { id: "1", username: "user-one", createdAt: "" },
            token: "t",
            isAuthenticated: true,
            isLoading: false,
            login: vi.fn(),
            logout: vi.fn(),
          }}
        >
          <WorkItemHubProvider>
            <SchedulerChip />
            <MobileTabBar />
          </WorkItemHubProvider>
        </AuthContext.Provider>
      </MemoryRouter>,
    );

    // The scheduler's change and the Needs-you count's hint, both on the shared hub.
    expect(Object.keys(shared.handlers).sort()).toEqual([
      "SchedulerStateChanged",
      "WorkItemStateChanged",
    ]);
    expect(Object.keys(unused.handlers)).toEqual([]);
    // Only the provider asked for the hub by URL, once: no hook opened its own.
    expect(useSignalR.mock.calls.filter(([url]) => url !== null)).toEqual([["/hubs/work-item"]]);
  });
});
