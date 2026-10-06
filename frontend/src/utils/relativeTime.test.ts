import { describe, expect, test } from "vite-plus/test";
import { formatRelativeTime } from "./relativeTime";

const NOW = Date.parse("2026-06-15T12:00:00Z");
const ago = (ms: number) => new Date(NOW - ms).toISOString();
const MIN = 60_000;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;

describe("formatRelativeTime", () => {
  test.each([
    { name: "this instant", iso: ago(0), expected: "just now" },
    { name: "seconds ago", iso: ago(10_000), expected: "just now" },
    { name: "minutes ago", iso: ago(5 * MIN), expected: "5 min ago" },
    { name: "hours ago", iso: ago(3 * HOUR), expected: "3 h ago" },
    { name: "days ago", iso: ago(2 * DAY), expected: "2 d ago" },
  ])("$name -> $expected", ({ iso, expected }) => {
    expect(formatRelativeTime(iso, NOW)).toBe(expected);
  });

  test("anything long past is shown as its locale date", () => {
    const iso = ago(400 * DAY);
    expect(formatRelativeTime(iso, NOW)).toBe(new Date(iso).toLocaleDateString());
  });
});
