import { describe, expect, it } from "vite-plus/test";
import type { AnalyticsSeriesPoint } from "../../types";
import { fillBuckets } from "../analyticsBuckets";

const point = (periodStart: string, costUsd = 1): AnalyticsSeriesPoint => ({
  periodStart,
  runs: 1,
  inputTokens: 0,
  outputTokens: 0,
  costUsd,
});

describe("fillBuckets", () => {
  it("puts a gap where a day had no runs, so the axis is real time", () => {
    const filled = fillBuckets([point("2026-08-07"), point("2026-08-10")], "Day");
    expect(filled.map((b) => [b.periodStart, b.point !== null])).toEqual([
      ["2026-08-07", true],
      ["2026-08-08", false],
      ["2026-08-09", false],
      ["2026-08-10", true],
    ]);
  });

  it("steps by week and by month", () => {
    expect(
      fillBuckets([point("2026-08-03"), point("2026-08-17")], "Week").map((b) => b.periodStart),
    ).toEqual(["2026-08-03", "2026-08-10", "2026-08-17"]);
    expect(
      fillBuckets([point("2026-01-01"), point("2026-03-01")], "Month").map((b) => b.periodStart),
    ).toEqual(["2026-01-01", "2026-02-01", "2026-03-01"]);
  });

  it("keeps the order whatever order the server sent, and an empty series stays empty", () => {
    expect(
      fillBuckets([point("2026-08-09"), point("2026-08-08")], "Day").map((b) => b.periodStart),
    ).toEqual(["2026-08-08", "2026-08-09"]);
    expect(fillBuckets([], "Day")).toEqual([]);
  });

  it("keeps a bucket that falls off the week grid rather than dropping it", () => {
    const filled = fillBuckets(
      [point("2026-08-03"), point("2026-08-05"), point("2026-08-10")],
      "Week",
    );
    expect(filled.map((b) => b.periodStart)).toEqual(["2026-08-03", "2026-08-05", "2026-08-10"]);
  });
});
