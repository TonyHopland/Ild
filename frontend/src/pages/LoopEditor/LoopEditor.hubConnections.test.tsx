import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType, RecoveryPolicy } from "../../types";

// Every hub connection the page opens, by URL. The stand-in connects at once,
// so each hook gets as far as listening.
const { builtFor } = vi.hoisted(() => ({ builtFor: [] as string[] }));

vi.mock("@microsoft/signalr", () => ({
  HubConnectionBuilder: class {
    private url = "";
    withUrl(url: string) {
      this.url = url;
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      builtFor.push(this.url);
      let state = "Disconnected";
      return {
        start: async () => {
          state = "Connected";
        },
        stop: async () => {
          state = "Disconnected";
        },
        invoke: async () => undefined,
        on: () => {},
        onreconnecting: () => {},
        onreconnected: () => {},
        onclose: () => {},
        get state() {
          return state;
        },
      };
    }
  },
  HubConnectionState: {
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Disconnecting: "Disconnecting",
    Reconnecting: "Reconnecting",
  },
}));

import LoopEditor from "./index";
import { reply, stubFetch } from "../../test-support";

const template = {
  id: "tpl-1",
  name: "Dev Loop",
  description: "",
  version: 1,
  recoveryPolicy: RecoveryPolicy.AutoResume,
  nodes: [
    { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
    { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
  ],
  edges: [
    { id: "e-1", sourceNodeId: "n-start", targetNodeId: "n-cleanup", edgeType: EdgeType.OnSuccess },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
  isArchived: false,
};

const asked: string[] = [];

beforeEach(() => {
  builtFor.length = 0;
  asked.length = 0;
  localStorage.setItem("auth_token", "test-token");
  stubFetch(({ path }) => {
    asked.push(path);
    return reply(
      200,
      path === "/api/v1/looptemplates"
        ? [template]
        : path.endsWith("/pending-counts") || path.endsWith("/node-outputs")
          ? {}
          : [],
    );
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe("the Loop Editor's hub connections", () => {
  test("the proposal counts, the open template's proposals and the review share one work-item hub connection", async () => {
    render(
      <MemoryRouter initialEntries={["/loop-editor/tpl-1"]}>
        <AuthContext.Provider
          value={{
            user: { id: "1", username: "test", createdAt: "" },
            token: "test-token",
            isAuthenticated: true,
            isLoading: false,
            login: vi.fn(),
            logout: vi.fn(),
          }}
        >
          <Routes>
            <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
          </Routes>
        </AuthContext.Provider>
      </MemoryRouter>,
    );

    await waitFor(() =>
      expect(asked).toEqual(
        expect.arrayContaining([
          "/api/v1/loop-template-proposals/pending-counts",
          "/api/v1/loop-template-proposals",
        ]),
      ),
    );
    expect(builtFor.filter((url) => url === "/hubs/work-item")).toHaveLength(1);
  });
});
