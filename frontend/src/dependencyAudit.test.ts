import { describe, expect, test } from "vite-plus/test";
// Loaded as a raw string so the test reads the actual resolved dependency tree
// without needing Node's filesystem types in this DOM-only project.
import lockfile from "../../pnpm-lock.yaml?raw";

/**
 * Collects every resolved version of a package from the lockfile's `packages`
 * section, pre-releases included. Scoped names are keys pnpm quotes
 * ('@scope/name@1.2.3':).
 */
function resolvedVersions(name: string): string[] {
  const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const pattern = new RegExp(`^ {2}'?${escaped}@(\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?)'?:`, "gm");
  const versions = [...lockfile.matchAll(pattern)].map((match) => match[1]);
  return [...new Set(versions)];
}

// Semver precedence: a pre-release sorts below its release (5.0.0-beta.3 < 5.0.0),
// so a release floor rejects the betas an advisory covers.
function isAtLeast(version: string, minimum: string): boolean {
  const [aCore, ...aRest] = version.split("-");
  const [bCore, ...bRest] = minimum.split("-");
  const a = aCore.split(".").map(Number);
  const b = bCore.split(".").map(Number);
  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) return a[i] > b[i];
  }
  if (aRest.length === 0) return true;
  if (bRest.length === 0) return false;
  const aPre = aRest.join("-").split(".");
  const bPre = bRest.join("-").split(".");
  for (let i = 0; i < Math.max(aPre.length, bPre.length); i++) {
    if (aPre[i] === undefined) return false;
    if (bPre[i] === undefined) return true;
    if (aPre[i] === bPre[i]) continue;
    const aNum = /^\d+$/.test(aPre[i]);
    const bNum = /^\d+$/.test(bPre[i]);
    if (aNum && bNum) return Number(aPre[i]) > Number(bPre[i]);
    if (aNum !== bNum) return bNum;
    return aPre[i] > bPre[i];
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
const patchedFloors: {
  name: string;
  floors: Record<number, string>;
  advisory: string;
}[] = [
  // GHSA-8x6r-g9mw-2r78 (DoS), GHSA-84g9-w2xq-vcv6 (CSRF) and GHSA-qwww-vcr4-c8h2
  // (RSC-mode CSRF). The last is patched only in 8.3.0, and no 7.x carries the fix,
  // so the floor is the whole 8 major: v8 drops the `react-router-dom` re-export
  // shim, and depending on it again is what would strand this back on 7.x.
  {
    name: "react-router",
    floors: { 8: "8.3.0" },
    advisory: "GHSA-8x6r-g9mw-2r78 / GHSA-84g9-w2xq-vcv6 / GHSA-qwww-vcr4-c8h2",
  },
  // ws@7 GHSA-96hv-2xvq-fx4p (patched 7.5.11); ws@8 GHSA-58qx-3vcg-4xpx /
  // memory-exhaustion DoS (patched 8.21.0).
  {
    name: "ws",
    floors: { 7: "7.5.11", 8: "8.21.0" },
    advisory: "GHSA-96hv-2xvq-fx4p / GHSA-58qx-3vcg-4xpx",
  },
  // vite GHSA-fx2h-pf6j-xcff and launch-editor NTLMv2 disclosure (patched 8.0.16).
  // Vite+ aliases vite to vite-plus-core, which bundles the Vite that runs, so the
  // floor is the first core whose package.json bundledVersions.vite is 8.0.16
  // (0.1.24); every 1.x release bundles 8.3.1 or later.
  {
    name: "@voidzero-dev/vite-plus-core",
    floors: { 0: "0.1.24", 1: "1.0.0" },
    advisory: "GHSA-fx2h-pf6j-xcff / GHSA-v6wh-96g9-6wx3",
  },
  // postcss GHSA-r28c-9q8g-f849 (path traversal), patched 8.5.18. Reached through
  // the vite-plus toolchain, so a toolchain downgrade is what would reintroduce it.
  { name: "postcss", floors: { 8: "8.5.18" }, advisory: "GHSA-r28c-9q8g-f849" },
  // undici 7.x carries seven advisories with no patched 7.x release, so the fix was
  // the major itself: jsdom 30 moved to undici 8. The floor is therefore the whole
  // major — any 7.x copy reappearing is vulnerable no matter its patch level.
  {
    name: "undici",
    floors: { 8: "8.9.0" },
    advisory: "GHSA-vxpw-j846-p89q / GHSA-hm92-r4w5-c3mj / GHSA-vmh5-mc38-953g",
  },
  // vitest and @vitest/mocker GHSA-82fw-gwwq-j7x9, patched 4.1.11. Both arrive with
  // the vite-plus toolchain; Vite+ 1.x ships vitest 5, where only the pre-release
  // betas before 5.0.0-rc.2 were affected. The 5.0.0 floor admits every stable 5.x
  // and rejects every 5.0.0 pre-release, the affected betas among them.
  { name: "vitest", floors: { 4: "4.1.11", 5: "5.0.0" }, advisory: "GHSA-82fw-gwwq-j7x9" },
  {
    name: "@vitest/mocker",
    floors: { 4: "4.1.11", 5: "5.0.0" },
    advisory: "GHSA-82fw-gwwq-j7x9",
  },
  // nanoid GHSA-2v37-7h3g-55p8 (a zero-size custom generator loops forever),
  // patched 3.3.18 and held there by a pnpm-workspace.yaml override.
  { name: "nanoid", floors: { 3: "3.3.18" }, advisory: "GHSA-2v37-7h3g-55p8" },
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

  test("no standalone vite package bypasses the vite-plus-core alias", () => {
    expect(
      resolvedVersions("vite"),
      "a real vite copy is a second Vite instance beside vite-plus-core, which breaks identity checks and type-checking of vite.config.ts",
    ).toEqual([]);
  });

  test.each([
    ["5.0.0-beta.3", "5.0.0", false],
    ["5.0.0", "5.0.0-rc.2", true],
    ["5.0.0-rc.2", "5.0.0-beta.1", true],
    ["5.0.0-beta.2", "5.0.0-beta.10", false],
    ["5.0.0-beta", "5.0.0-beta.1", false],
    ["5.0.0-1", "5.0.0-alpha", false],
    ["5.0.0-alpha", "5.0.0-1", true],
    ["5.0.0-rc.1", "5.0.0-rc.1", true],
    ["5.0.0-x-y.2", "5.0.0-x-y.10", false],
    ["5.0.1", "5.0.0", true],
    ["4.1.10", "4.1.11", false],
  ])("isAtLeast(%s, %s) is %s", (version, minimum, expected) => {
    expect(isAtLeast(version, minimum)).toBe(expected);
  });

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
