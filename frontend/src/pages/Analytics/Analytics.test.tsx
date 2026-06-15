import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthContext } from "../../hooks/useAuth";
import { RunAnalyticsOverview } from "../../types";
import Analytics from "./index";

afterEach(() => {
  cleanup();
});

function mockFetch(json: unknown, status = 200) {
  return vi.fn().mockResolvedValue({
    ok: status < 400,
    status,
    text: () => Promise.resolve(JSON.stringify(json)),
  });
}

function renderPage(mockFetchFn: ReturnType<typeof mockFetch>) {
  vi.stubGlobal("fetch", mockFetchFn);
  const authValue = {
    user: { id: "1", username: "test", createdAt: "" },
    token: "test-token",
    isAuthenticated: true,
    isLoading: false,
    login: vi.fn(),
    logout: vi.fn(),
  };
  render(
    <MemoryRouter>
      <AuthContext.Provider value={authValue}>
        <Analytics />
      </AuthContext.Provider>
    </MemoryRouter>,
  );
}

const overview: RunAnalyticsOverview = {
  totalRuns: 3,
  totalInputTokens: 12_300,
  totalOutputTokens: 4_000,
  totalCostUsd: 1.25,
  templates: [
    {
      templateId: "tmpl-1",
      templateName: "coder-loop",
      totalRuns: 3,
      completedRuns: 2,
      failedRuns: 1,
      cancelledRuns: 0,
      successRate: 0.6666,
      avgNodeSeconds: 12.5,
      onFailureRoutings: 4,
      rejectRoutings: 1,
      avgHumanFeedbackSeconds: 90,
      totalInputTokens: 12_300,
      totalOutputTokens: 4_000,
      totalCostUsd: 1.25,
    },
  ],
};

describe("Analytics page", () => {
  test("renders account totals and per-template figures from real run data", async () => {
    renderPage(mockFetch(overview));

    // The template name appears in both the cost chart and its card.
    await waitFor(() => expect(screen.getAllByText("coder-loop").length).toBeGreaterThan(0));

    // Account-wide cost summary (also echoed in the cost-by-template chart).
    expect(screen.getAllByText("$1.25").length).toBeGreaterThan(0);
    expect(screen.getByText("12.3k")).toBeTruthy(); // input-tokens summary card

    // Per-template success rate and time-per-node.
    expect(screen.getByText("67% success")).toBeTruthy();
    expect(screen.getByText("13s")).toBeTruthy(); // avg/node, 12.5s rounds to 13s
  });

  test("shows an empty-state message when no run data exists yet", async () => {
    renderPage(
      mockFetch({
        totalRuns: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCostUsd: 0,
        templates: [],
      } satisfies RunAnalyticsOverview),
    );

    await waitFor(() => expect(screen.getByText(/No run data yet/)).toBeTruthy());
  });
});
