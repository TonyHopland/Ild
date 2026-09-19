import { describe, expect, test } from "vite-plus/test";
import { attachedNote } from "./attachments";

describe("what an answer records of the typed text", () => {
  test("keeps it exactly as typed, whether or not files are attached", () => {
    const typed = "  Please look at the margins.\n\nThe second one especially.  ";

    expect(attachedNote(typed, [])).toBe(typed);
    expect(attachedNote(typed, ["a.png"])).toBe(`${typed}\n\nAttached files: a.png`);
  });

  test("puts the list on its own when there is nothing but whitespace to keep", () => {
    expect(attachedNote("   \n ", ["a.png"])).toBe("Attached files: a.png");
  });
});
