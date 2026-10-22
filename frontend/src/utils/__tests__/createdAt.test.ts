import { describe, expect, test } from "vite-plus/test";
import { compareCreatedAt } from "../createdAt";

function oldestFirst(times: string[]): string[] {
  return [...times].sort(compareCreatedAt);
}

describe("compareCreatedAt at the server's precision", () => {
  test("orders creation times inside one millisecond by their 100 ns ticks", () => {
    expect(
      oldestFirst([
        "2026-01-01T00:00:00.1230009Z",
        "2026-01-01T00:00:00.123Z",
        "2026-01-01T00:00:00.1230001Z",
      ]),
    ).toEqual([
      "2026-01-01T00:00:00.123Z",
      "2026-01-01T00:00:00.1230001Z",
      "2026-01-01T00:00:00.1230009Z",
    ]);
  });

  test("reads a missing fraction as zero and keeps the zone of the whole seconds", () => {
    expect(
      oldestFirst([
        "2026-01-01T02:00:00.0000001+02:00",
        "2026-01-01T00:00:00Z",
        "2025-12-31T23:59:59.9999999Z",
      ]),
    ).toEqual([
      "2025-12-31T23:59:59.9999999Z",
      "2026-01-01T00:00:00Z",
      "2026-01-01T02:00:00.0000001+02:00",
    ]);
  });

  test("an equal time at another precision compares equal", () => {
    expect(compareCreatedAt("2026-01-01T00:00:00.5Z", "2026-01-01T00:00:00.5000000Z")).toBe(0);
  });
});
