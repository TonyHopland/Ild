import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, fireEvent, cleanup } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { NodeType, EdgeType } from "../../types";
import LoopEditor from "./index";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const sampleTemplate = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Dev Loop",
  description: "Standard development loop",
  version: 3,
  nodes: [
    {
      id: "n-start",
      type: NodeType.Start,
      label: "Initialize",
      config: {},
    },
    {
      id: "n-cleanup",
      type: NodeType.Cleanup,
      label: "Tidy Up",
      config: {},
    },
  ],
  edges: [
    {
      id: "e-1",
      sourceNodeId: "n-start",
      targetNodeId: "n-cleanup",
      edgeType: EdgeType.OnSuccess,
    },
  ],
  createdAt: "2025-01-01T00:00:00Z",
  updatedAt: "2025-01-01T00:00:00Z",
};

describe("Loop Editor save existing template (regression)", () => {
  test.each([
    ["without __pos", false],
    ["with __pos config (round-trip)", true],
  ])(
    "saving an existing template %s completes and does not hang",
    async (_, withPos) => {
      const stored = withPos
        ? {
            ...sampleTemplate,
            nodes: sampleTemplate.nodes.map((n, i) => ({
              ...n,
              config: { __pos: { x: 100 + i * 50, y: 80 } },
            })),
          }
        : sampleTemplate;
      const fetchCalls: Array<{ url: string; method: string }> = [];

      const trackingFetch = vi.fn(async (url: string, init?: RequestInit) => {
        const method = (init?.method as string) ?? "GET";
        const urlStr = String(url);
        fetchCalls.push({ url: urlStr, method });
        const ok = (body: unknown) => ({
          ok: true,
          status: 200,
          text: () => Promise.resolve(JSON.stringify(body)),
        });

        if (method === "GET" && urlStr.includes("/looptemplates") && !urlStr.includes("versions"))
          return ok([stored]);
        if (method === "POST" && urlStr.includes("/looptemplates/validate"))
          return ok({ valid: true, errors: [] });
        if (method === "PUT" && urlStr.includes("/looptemplates/")) return ok({ id: stored.id });
        if (method === "GET" && urlStr.includes("/aiproviders")) return ok([]);
        return { ok: false, status: 500, text: () => Promise.resolve("") };
      });

      vi.stubGlobal("fetch", trackingFetch);

      const authValue = {
        user: { id: "1", username: "test", createdAt: "" },
        token: "test-token",
        isAuthenticated: true,
        isLoading: false,
        login: vi.fn(),
        logout: vi.fn(),
      };

      render(
        <MemoryRouter initialEntries={["/loop-editor"]}>
          <AuthContext.Provider value={authValue}>
            <Routes>
              <Route path="/loop-editor" element={<LoopEditor />} />
              <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
            </Routes>
          </AuthContext.Provider>
        </MemoryRouter>,
        { wrapper: WorkItemHubProvider },
      );

      await waitFor(() => expect(screen.getByText("Dev Loop")).toBeTruthy());
      fireEvent.click(screen.getByText("Dev Loop"));
      await waitFor(() => expect(screen.getByText("Initialize")).toBeTruthy());

      fireEvent.click(screen.getByText("Save"));
      fireEvent.click(await screen.findByText("Save changes"));
      await waitFor(() => expect(fetchCalls.some((c) => c.method === "PUT")).toBe(true), {
        timeout: 5000,
      });
      await waitFor(() => expect(screen.getByText("Saved!")).toBeTruthy(), { timeout: 5000 });

      fireEvent.click(screen.getByText("Save"));
      fireEvent.click(await screen.findByText("Save changes"));
      await waitFor(() => expect(fetchCalls.filter((c) => c.method === "PUT").length).toBe(2), {
        timeout: 5000,
      });
    },
    15000,
  );
});
