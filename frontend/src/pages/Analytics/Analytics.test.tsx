import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent } from "@testing-library/react";
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
  granularity: "Day",
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
  providers: [
    {
      provider: "claude",
      totalRuns: 3,
      totalInputTokens: 12_300,
      totalOutputTokens: 4_000,
      totalCostUsd: 1.25,
    },
  ],
  series: [
    { periodStart: "2026-06-01", runs: 3, inputTokens: 12_300, outputTokens: 4_000, costUsd: 1.25 },
  ],
  availableProviders: ["claude", "opencode"],
};

function lastFetchUrl(fetchFn: ReturnType<typeof mockFetch>): string {
  const calls = fetchFn.mock.calls;
  return String(calls[calls.length - 1][0]);
}

describe("Analytics page", () => {
  test("renders totals, per-template, provider breakdown, and the time series", async () => {
    renderPage(mockFetch(overview));

    await waitFor(() => expect(screen.getAllByText("coder-loop").length).toBeGreaterThan(0));

    // Account-wide cost summary (also echoed in the cost-by-template chart).
    expect(screen.getAllByText("$1.25").length).toBeGreaterThan(0);
    expect(screen.getByText("12.3k")).toBeTruthy(); // input-tokens summary card

    // Per-template success rate and time-per-node.
    expect(screen.getByText("67% success")).toBeTruthy();
    expect(screen.getByText("13s")).toBeTruthy(); // avg/node, 12.5s rounds to 13s

    // Provider breakdown and time series sections render. "claude" appears both
    // as a chart bar label and a filter <option>, so assert at least one.
    expect(screen.getByText("By agent provider")).toBeTruthy();
    expect(screen.getByText("Cost over time")).toBeTruthy();
    expect(screen.getAllByText("claude").length).toBeGreaterThan(0);
  });

  test("changing granularity refetches with the granularity query param", async () => {
    const fetchFn = mockFetch(overview);
    renderPage(fetchFn);
    await waitFor(() => expect(screen.getAllByText("coder-loop").length).toBeGreaterThan(0));

    fireEvent.click(screen.getByRole("button", { name: "Week" }));

    await waitFor(() => expect(lastFetchUrl(fetchFn)).toContain("granularity=Week"));
  });

  test("selecting a provider refetches with the provider query param", async () => {
    const fetchFn = mockFetch(overview);
    renderPage(fetchFn);
    await waitFor(() => expect(screen.getAllByText("coder-loop").length).toBeGreaterThan(0));

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "opencode" } });

    await waitFor(() => expect(lastFetchUrl(fetchFn)).toContain("provider=opencode"));
  });

  test("shows an empty-state message when no run data exists yet", async () => {
    renderPage(
      mockFetch({
        totalRuns: 0,
        totalInputTokens: 0,
        totalOutputTokens: 0,
        totalCostUsd: 0,
        granularity: "Day",
        templates: [],
        providers: [],
        series: [],
        availableProviders: [],
      } satisfies RunAnalyticsOverview),
    );

    await waitFor(() => expect(screen.getByText(/No run data/)).toBeTruthy());
  });
});
