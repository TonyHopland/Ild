import { describe, expect, test } from "vite-plus/test";
import { formatCost, formatPercent, formatTokens } from "../cost";

describe("formatTokens", () => {
  test("formats small, thousand, and million scales", () => {
    expect(formatTokens(0)).toBe("0");
    expect(formatTokens(940)).toBe("940");
    expect(formatTokens(12_300)).toBe("12.3k");
    expect(formatTokens(1_200_000)).toBe("1.2M");
  });

  test("treats missing or invalid counts as zero", () => {
    expect(formatTokens(null)).toBe("0");
    expect(formatTokens(undefined)).toBe("0");
    expect(formatTokens(-5)).toBe("0");
  });
});

describe("formatCost", () => {
  test("uses two decimals for normal amounts", () => {
    expect(formatCost(1.234)).toBe("$1.23");
    expect(formatCost(0)).toBe("$0.00");
  });

  test("keeps four decimals for sub-cent amounts so cheap runs are visible", () => {
    expect(formatCost(0.0012)).toBe("$0.0012");
  });

  test("returns null when no cost was reported", () => {
    expect(formatCost(null)).toBeNull();
    expect(formatCost(undefined)).toBeNull();
    expect(formatCost(-1)).toBeNull();
  });
});

describe("formatPercent", () => {
  test("rounds a fraction to a whole percent", () => {
    expect(formatPercent(0.666)).toBe("67%");
    expect(formatPercent(1)).toBe("100%");
  });

  test("renders an em dash when undefined", () => {
    expect(formatPercent(null)).toBe("—");
    expect(formatPercent(undefined)).toBe("—");
  });
});
