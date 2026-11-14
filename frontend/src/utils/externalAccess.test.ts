import { describe, expect, test } from "vite-plus/test";
import { buildAccessInstructions, formatTimeLeft, mcpUrl } from "./externalAccess";

describe("buildAccessInstructions", () => {
  const access = {
    baseUrl: "https://ild.example.com",
    token: "ildg_secret",
    expiresAt: "2030-01-01T12:00:00Z",
    scope: "Full" as const,
  };

  test("names the base URL, the token, both transports and the discovery document", () => {
    const text = buildAccessInstructions(access);

    expect(text).toContain("Base URL: https://ild.example.com");
    expect(text).toContain("Token: ildg_secret");
    expect(text).toContain('"Authorization: Bearer ildg_secret"');
    expect(text).toContain("https://ild.example.com/api/v1/agent/...");
    expect(text).toContain(mcpUrl(access.baseUrl));
    expect(text).toContain("https://ild.example.com/api/v1/agent/_about");
    expect(text).toContain("2030-01-01T12:00:00.000Z");
  });

  test("says what a read-only grant cannot do", () => {
    expect(buildAccessInstructions({ ...access, scope: "ReadOnly" })).toContain("read-only");
    expect(buildAccessInstructions(access)).not.toContain("read-only");
  });
});

describe("formatTimeLeft", () => {
  const now = Date.parse("2030-01-01T00:00:00Z");

  test.each([
    ["2029-12-31T23:59:00Z", "expired"],
    ["2030-01-01T00:00:30Z", "1 min left"],
    ["2030-01-01T00:45:00Z", "45 min left"],
    ["2030-01-01T02:05:00Z", "2 h 5 min left"],
    ["2030-01-03T03:00:00Z", "2 d 3 h left"],
  ])("%s reads %s", (expiresAt, expected) => {
    expect(formatTimeLeft(expiresAt, now)).toBe(expected);
  });
});
