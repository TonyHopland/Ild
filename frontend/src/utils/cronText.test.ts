import { describe, expect, test } from "vite-plus/test";
import { describeCron } from "./cronText";

describe("describeCron", () => {
  test.each([
    ["0 * * * *", [/hour/i]],
    ["15 * * * *", [/hour/i, /\b15\b/]],
    ["30 9 * * *", [/day|daily/i, /\b0?9:30\b/]],
    ["0 8 * * 1", [/monday/i, /\b0?8:00\b/]],
    ["45 17 * * 5", [/friday/i, /\b17:45\b/]],
    ["0 8 * * 0", [/sunday/i]],
    ["0 8 * * 7", [/sunday/i]],
  ])("puts the preset shape %s in plain words", (expression, patterns) => {
    const text = describeCron(expression);
    expect(text).not.toBe(expression);
    for (const pattern of patterns) expect(text).toMatch(pattern);
  });

  test.each([
    "0 8 1 * *",
    "*/15 * * * *",
    "0 */2 * * *",
    "0 8 * * 1-5",
    "0 8 * 6 1",
    "0 8,17 * * *",
  ])("shows %s, which is no preset shape, as the raw cron", (expression) => {
    expect(describeCron(expression)).toBe(expression);
  });
});
