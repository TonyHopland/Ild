import { describe, expect, test } from "vite-plus/test";
import { formatDuration, formatDurationMs } from "../duration";

describe("formatDurationMs", () => {
  test("formats sub-minute spans in seconds", () => {
    expect(formatDurationMs(0)).toBe("0s");
    expect(formatDurationMs(45_000)).toBe("45s");
  });

  test("formats minute and hour spans", () => {
    expect(formatDurationMs(90_000)).toBe("1m 30s");
    expect(formatDurationMs(2 * 3_600_000 + 5 * 60_000)).toBe("2h 5m");
  });

  test("returns null for negative or non-finite spans", () => {
    expect(formatDurationMs(-1)).toBeNull();
    expect(formatDurationMs(Number.NaN)).toBeNull();
  });
});

describe("formatDuration", () => {
  test("formats the span between two timestamps", () => {
    expect(formatDuration("2026-06-14T12:00:00Z", "2026-06-14T12:01:30Z")).toBe("1m 30s");
  });

  test("returns null when either bound is missing", () => {
    expect(formatDuration(null, "2026-06-14T12:00:00Z")).toBeNull();
    expect(formatDuration("2026-06-14T12:00:00Z", null)).toBeNull();
  });
});
