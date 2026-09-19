import { describe, expect, test } from "vite-plus/test";
import { attachedNote, formatBytes, oversizeMessage, stagedFileName } from "./attachments";

const MB = 1024 * 1024;

describe("formatBytes", () => {
  test("reports a size in the unit a human reads it in", () => {
    expect(formatBytes(900)).toContain("900");
    expect(formatBytes(900)).not.toMatch(/KB|MB/);
    expect(formatBytes(2 * 1024)).toMatch(/2.*KB/);
    expect(formatBytes(25 * MB)).toMatch(/25.*MB/);
  });
});

describe("oversizeMessage", () => {
  // The limit has to be in the message: "too large" alone tells the human
  // nothing about which file to shrink or by how much.
  test("names the file and the configured maximum", () => {
    const message = oversizeMessage("big.bin", 25 * MB);

    expect(message).toContain("big.bin");
    expect(message).toContain("25 MB");
  });
});

describe("attachedNote", () => {
  test("lists the attached file names under whatever the human typed", () => {
    expect(attachedNote("Looks good", ["a.png", "b.pdf"])).toBe(
      "Looks good\n\nAttached files: a.png, b.pdf",
    );
  });

  test("is the list alone when nothing was typed", () => {
    expect(attachedNote("", ["a.png"])).toBe("Attached files: a.png");
  });

  test("leaves the typed text alone when no file is attached", () => {
    expect(attachedNote("Looks good", [])).toBe("Looks good");
  });
});

describe("stagedFileName", () => {
  const now = new Date("2026-09-18T10:20:30Z");

  test("keeps the name a picked file already has", () => {
    const file = new File(["x"], "screenshot.png", { type: "image/png" });

    expect(stagedFileName(file, now)).toBe("screenshot.png");
  });

  // A pasted screenshot can arrive without a name, and the server's own
  // fallback is the literal "attachment" — which would make every pasted
  // image indistinguishable on the work item.
  test("names a nameless pasted file after its type", () => {
    const name = stagedFileName(new File(["x"], "", { type: "image/png" }), now);

    expect(name.endsWith(".png")).toBe(true);
    expect(name).not.toBe("attachment");
    expect(name.length).toBeGreaterThan(".png".length);
  });
});
