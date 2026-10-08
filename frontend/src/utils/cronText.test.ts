import { describe, expect, test } from "vite-plus/test";
import { cronText } from "./cronText";

describe("cronText", () => {
  test.each([
    ["0 * * * *", [/every hour|hourly/i]],
    ["15 * * * *", [/hour/i, /\b15\b/]],
    ["30 9 * * *", [/every day|daily/i, /\b0?9:30\b/]],
    ["0 8 * * 1", [/monday/i, /\b0?8:00\b/]],
    ["45 17 * * 5", [/friday/i, /\b17:45\b/]],
    ["0 8 * * 0", [/sunday/i]],
    ["0 8 * * 7", [/sunday/i]],
  ])("puts %s in plain words", (expression, patterns) => {
    const text = cronText(expression);
    expect(text).not.toBe(expression);
    for (const pattern of patterns) expect(text).toMatch(pattern);
  });

  test("never describes a cron outside those shapes as one of them", () => {
    expect(cronText("0 8 1 * *") ?? "").not.toMatch(/every day|daily/i);
    expect(cronText("*/15 * * * *") ?? "").not.toMatch(/every hour|hourly/i);
    expect(cronText("0 8 * * 1-5") ?? "").not.toMatch(/^every monday/i);
    expect(cronText("0 8 * 6 1") ?? "").not.toMatch(/^every monday/i);
  });
});
