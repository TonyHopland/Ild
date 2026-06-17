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

// Each entry guards a fixed Dependabot advisory. Resolving a dependency below the
// patched floor must fail this test.
const patchedFloors: { name: string; minimum: string; advisory: string }[] = [
  // GHSA-8x6r-g9mw-2r78 (DoS) and GHSA-84g9-w2xq-vcv6 (CSRF).
  {
    name: "react-router",
    minimum: "7.15.1",
    advisory: "GHSA-8x6r-g9mw-2r78 / GHSA-84g9-w2xq-vcv6",
  },
  {
    name: "react-router-dom",
    minimum: "7.15.1",
    advisory: "GHSA-8x6r-g9mw-2r78 / GHSA-84g9-w2xq-vcv6",
  },
  // GHSA-96hv-2xvq-fx4p (memory-exhaustion DoS), patched in 7.5.11.
  { name: "ws", minimum: "7.5.11", advisory: "GHSA-96hv-2xvq-fx4p" },
];

describe("dependency security audit", () => {
  test.each(patchedFloors)(
    "$name is resolved at or above the $minimum patched floor",
    ({ name, minimum }) => {
      const versions = resolvedVersions(name);
      expect(versions.length).toBeGreaterThan(0);
      for (const version of versions) {
        expect(isAtLeast(version, minimum)).toBe(true);
      }
    },
  );

  test("every resolved ws version is patched, including transitive majors", () => {
    // signalr pulls ws@7 while other tooling pulls ws@8 — both must clear the
    // 7.5.11 floor, so the scoped override cannot silently leave a 7.x copy behind.
    const versions = resolvedVersions("ws");
    expect(versions.length).toBeGreaterThan(1);
    for (const version of versions) {
      expect(isAtLeast(version, "7.5.11")).toBe(true);
    }
  });
});
