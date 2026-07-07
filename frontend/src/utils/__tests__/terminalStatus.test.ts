import { describe, expect, test } from "vite-plus/test";
import { describeTerminalClose } from "../terminalStatus";

describe("describeTerminalClose", () => {
  test("clean 1000 close shows nothing, whether or not the socket had opened", () => {
    expect(describeTerminalClose(1000, true, "hint")).toBeNull();
    expect(describeTerminalClose(1000, false, "hint")).toBeNull();
  });

  test("abnormal 1006 close after opening never surfaces the raw code", () => {
    const message = describeTerminalClose(1006, true, "hint");
    expect(message).not.toBeNull();
    expect(message).not.toContain("1006");
    expect(message).not.toMatch(/\d{4}/);
  });

  test("abnormal close after opening reports a lost connection, ignoring the hint", () => {
    // The hint describes *connect* failures; a mid-session drop is different.
    expect(describeTerminalClose(1006, true, "check the binary")).toBe(
      "Connection lost. Close and reopen the terminal to reconnect.",
    );
  });

  test("abnormal close before opening surfaces the caller's connect hint", () => {
    expect(describeTerminalClose(1006, false, "check the binary is installed")).toBe(
      "check the binary is installed",
    );
  });

  test("abnormal close before opening falls back to a friendly default when no hint given", () => {
    expect(describeTerminalClose(1011, false)).toBe("Unable to connect.");
  });

  test("no user-facing message ever contains a raw close code", () => {
    for (const code of [1001, 1006, 1011, 1012, 4000]) {
      for (const hadOpened of [true, false]) {
        const message = describeTerminalClose(code, hadOpened);
        if (message) expect(message).not.toMatch(/\d{3,}/);
      }
    }
  });
});
