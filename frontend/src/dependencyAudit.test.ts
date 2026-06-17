import { describe, expect, test } from "vite-plus/test";
// Loaded as a raw string so the test reads the actual resolved dependency tree
// without needing Node's filesystem types in this DOM-only project.
import lockfile from "../../pnpm-lock.yaml?raw";

/** Collects every resolved version of a package from the lockfile's `packages` section. */
function resolvedVersions(name: string): string[] {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const pattern = new RegExp(`^ {2}${escaped}@(\\d+\\.\\d+\\.\\d+):`, "gm");
  const versions = [...lockfile.matchAll(pattern)].map((match) => match[1]);
  return [...new Set(versions)];
}

function isAtLeast(version: string, minimum: string): boolean {
  const a = version.split(".").map(Number);
  const b = minimum.split(".").map(Number);
  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) return a[i] > b[i];
  }
  return true;
}

function major(version: string): number {
  return Number(version.split(".")[0]);
}

// Each entry guards the Dependabot advisories for a package. A package can ship
// fixes on several majors, so floors are keyed by major version: every resolved
// copy must clear the floor for its own major, and an unexpected major (one with
// no floor) fails loudly rather than slipping through unverified.
const patchedFloors: { name: string; floors: Record<number, string>; advisory: string }[] = [
  // GHSA-8x6r-g9mw-2r78 (DoS) and GHSA-84g9-w2xq-vcv6 (CSRF).
  {
    name: "react-router",
    floors: { 7: "7.15.1" },
    advisory: "GHSA-8x6r-g9mw-2r78 / GHSA-84g9-w2xq-vcv6",
  },
  {
    name: "react-router-dom",
    floors: { 7: "7.15.1" },
    advisory: "GHSA-8x6r-g9mw-2r78 / GHSA-84g9-w2xq-vcv6",
  },
  // ws@7 GHSA-96hv-2xvq-fx4p (patched 7.5.11); ws@8 GHSA-58qx-3vcg-4xpx /
  // memory-exhaustion DoS (patched 8.21.0).
  {
    name: "ws",
    floors: { 7: "7.5.11", 8: "8.21.0" },
    advisory: "GHSA-96hv-2xvq-fx4p / GHSA-58qx-3vcg-4xpx",
  },
  // vite GHSA-fx2h-pf6j-xcff and launch-editor NTLMv2 disclosure (patched 8.0.16).
  { name: "vite", floors: { 8: "8.0.16" }, advisory: "GHSA-fx2h-pf6j-xcff / GHSA-v6wh-96g9-6wx3" },
];

describe("dependency security audit", () => {
  test.each(patchedFloors)(
    "$name clears its patched floor on every resolved major",
    ({ name, floors }) => {
      const versions = resolvedVersions(name);
      expect(versions.length).toBeGreaterThan(0);
      for (const version of versions) {
        const floor = floors[major(version)];
        // An unexpected major has no verified floor — fail rather than assume safe.
        expect(floor).toBeDefined();
        expect(isAtLeast(version, floor)).toBe(true);
      }
    },
  );

  test("ws is patched on both the 7.x and 8.x majors it resolves", () => {
    // signalr pulls ws@7 while the vite-plus toolchain pulls ws@8 — each major
    // has its own advisory, so each copy must clear its own floor (7.5.11 / 8.21.0),
    // not a single shared minimum that a vulnerable 8.20.x would also pass.
    const versions = resolvedVersions("ws");
    const majors = new Set(versions.map(major));
    expect(majors.has(7)).toBe(true);
    expect(majors.has(8)).toBe(true);
    for (const version of versions) {
      expect(isAtLeast(version, major(version) === 7 ? "7.5.11" : "8.21.0")).toBe(true);
    }
  });
});
