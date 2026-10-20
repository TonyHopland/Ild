import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router";
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
  chats: [],
};

const noRuns = {
  totalRuns: 0,
  totalInputTokens: 0,
  totalOutputTokens: 0,
  totalCostUsd: 0,
  granularity: "Day",
  templates: [],
  providers: [],
  series: [],
  availableProviders: [],
  chats: [],
} satisfies RunAnalyticsOverview;

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
    expect(screen.getByText("Cost per day")).toBeTruthy();
    expect(screen.getByRole("list", { name: "Cost over time" })).toBeTruthy();
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
    renderPage(mockFetch(noRuns));

    await waitFor(() => expect(screen.getByText(/No run data/)).toBeTruthy());
  });

  test("shows chat rows as their own cards and cost bars next to the templates", async () => {
    renderPage(
      mockFetch({
        ...overview,
        chats: [
          {
            scheduleId: null,
            scheduleName: null,
            totalTurns: 4,
            totalInputTokens: 2_500,
            totalOutputTokens: 300,
            totalCostUsd: 0.42,
          },
          {
            scheduleId: "sched-1",
            scheduleName: "Nightly retro",
            totalTurns: 2,
            totalInputTokens: 900,
            totalOutputTokens: 80,
            totalCostUsd: 0,
          },
        ],
      } satisfies RunAnalyticsOverview),
    );

    await waitFor(() =>
      expect(screen.getAllByText("Scheduled: Nightly retro").length).toBeGreaterThan(0),
    );

    expect(screen.getAllByText("coder-loop").length).toBeGreaterThan(0);
    // The plain chats' card, and their bar in the cost chart.
    expect(screen.getAllByText("Chat")).toHaveLength(2);
    expect(screen.getAllByText("$0.42")).toHaveLength(2);
    expect(screen.getByText("4 turns")).toBeTruthy();
    expect(screen.getByText("2.5k / 300")).toBeTruthy();
    expect(screen.getByText("2 turns")).toBeTruthy();
    expect(screen.getByText("900 / 80")).toBeTruthy();
  });

  test("shows chat usage instead of the empty state when there are no runs", async () => {
    renderPage(
      mockFetch({
        ...noRuns,
        totalInputTokens: 900,
        totalOutputTokens: 80,
        chats: [
          {
            scheduleId: "sched-1",
            scheduleName: "Nightly retro",
            totalTurns: 2,
            totalInputTokens: 900,
            totalOutputTokens: 80,
            totalCostUsd: 0,
          },
        ],
      } satisfies RunAnalyticsOverview),
    );

    await waitFor(() => expect(screen.getByText("Scheduled: Nightly retro")).toBeTruthy());

    expect(screen.queryByText(/No run data/)).toBeNull();
    expect(screen.getByText("2 turns")).toBeTruthy();
    expect(screen.getByText("900 / 80")).toBeTruthy();
    // No cost reported: the card shows a dash, as template cards do.
    expect(screen.getByText("—")).toBeTruthy();
  });

  test("the time series has a slot per day, a gap for a day without runs, and no bar for zero cost", async () => {
    renderPage(
      mockFetch({
        ...overview,
        series: [
          { periodStart: "2026-06-01", runs: 3, inputTokens: 0, outputTokens: 0, costUsd: 1.25 },
          { periodStart: "2026-06-03", runs: 1, inputTokens: 0, outputTokens: 0, costUsd: 0 },
        ],
      }),
    );

    const chart = await screen.findByRole("list", { name: "Cost over time" });
    const slots = Array.from(chart.querySelectorAll("[role=listitem]"));
    expect(slots).toHaveLength(3);
    expect(slots[1].getAttribute("aria-label")).toMatch(/no runs/);
    expect(slots[2].getAttribute("aria-label")).toMatch(/\$0\.00 · 1 run$/);
    expect((slots[2].firstElementChild as HTMLElement).style.height).toBe("0%");
    expect((slots[0].firstElementChild as HTMLElement).style.height).toBe("100%");
  });

  test("a template with no finished run shows — rather than 0% success, and counts one run in the singular", async () => {
    renderPage(
      mockFetch({
        ...overview,
        templates: [
          {
            ...overview.templates[0],
            totalRuns: 1,
            completedRuns: 0,
            failedRuns: 0,
            cancelledRuns: 0,
            successRate: 0,
          },
        ],
      }),
    );

    expect(await screen.findByText("— success")).toBeTruthy();
    expect(screen.getByText("1 run")).toBeTruthy();
  });
});
