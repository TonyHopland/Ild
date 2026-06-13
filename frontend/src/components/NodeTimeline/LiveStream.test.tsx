// @ts-expect-error node:fs has no bundled types in this frontend project, but
// the value is available at test runtime (vitest runs on Node).
import { readFileSync, existsSync } from "node:fs";
import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, act } from "@testing-library/react";

// Capture the xterm instance the component creates so we can assert on writes.
const writes: string[] = [];
const terminalMock = {
  loadAddon: vi.fn(),
  open: vi.fn(),
  write: vi.fn((s: string) => writes.push(s)),
  reset: vi.fn(() => {
    writes.length = 0;
  }),
  dispose: vi.fn(),
};

vi.mock("@xterm/xterm", () => ({
  Terminal: vi.fn(function Terminal() {
    return terminalMock;
  }),
}));
vi.mock("@xterm/addon-fit", () => ({
  FitAddon: vi.fn(function FitAddon() {
    return { fit: vi.fn() };
  }),
}));
vi.mock("@xterm/xterm/css/xterm.css", () => ({}));

import LiveStream from "./LiveStream";

afterEach(() => {
  cleanup();
  writes.length = 0;
  vi.clearAllMocks();
});

describe("LiveStream", () => {
  test("shows a placeholder and does not mount a terminal while empty", () => {
    render(<LiveStream text="" />);
    expect(screen.getByText("Waiting for output...")).toBeTruthy();
    expect(terminalMock.open).not.toHaveBeenCalled();
  });

  test("mounts a read-only terminal and writes the initial output once text arrives", () => {
    render(<LiveStream text={"hello\x1b[31m world\x1b[0m\n"} />);
    expect(terminalMock.open).toHaveBeenCalledTimes(1);
    // The full text (ANSI sequences intact) is written to the terminal.
    expect(writes.join("")).toBe("hello\x1b[31m world\x1b[0m\n");
  });

  test("writes only the appended delta on update, never re-writing prior bytes", () => {
    const { rerender } = render(<LiveStream text={"line one\n"} />);
    expect(writes.join("")).toBe("line one\n");

    act(() => rerender(<LiveStream text={"line one\nline two\n"} />));
    // Only the new suffix is written; "line one" is not written twice.
    expect(writes.join("")).toBe("line one\nline two\n");
    expect(terminalMock.write).toHaveBeenLastCalledWith("line two\n");
  });

  test("resets and rewrites when the stream restarts (text shrinks)", () => {
    const { rerender } = render(<LiveStream text={"old run output\n"} />);
    expect(writes.join("")).toBe("old run output\n");

    act(() => rerender(<LiveStream text={"new\n"} />));
    expect(terminalMock.reset).toHaveBeenCalled();
    expect(writes.join("")).toBe("new\n");
  });

  // Regression: xterm v6 wraps the viewport/screen in an extra
  // `.xterm-scrollable-element`. If only the viewport/screen are pinned to the
  // container height, that wrapper keeps its auto height and grows past the
  // viewport, so the last line of output is clipped by the fixed-height
  // container. The wrapper must be pinned to 100% alongside its siblings.
  test("pins the xterm scroll wrapper to the container so the last line is visible", () => {
    // Read the real stylesheet from disk. Importing the .css here is not an
    // option: vite-plus resolves `?raw` for stylesheets to an empty string. The
    // path is resolved against the runner's working directory, which is either
    // the repo root or the frontend package depending on how tests are launched.
    const relPath = "src/components/NodeTimeline/NodeTimeline.css";
    const cssPath = [`frontend/${relPath}`, relPath].find((p) => existsSync(p));
    expect(cssPath).toBeTruthy();
    const css: string = readFileSync(cssPath!, "utf8");

    const sizingRule = css
      .split("}")
      .find(
        (block) =>
          block.includes(".livestream-container .xterm") &&
          /height:\s*100%\s*!important/.test(block),
      );

    expect(sizingRule).toBeTruthy();
    const selectors = sizingRule!.slice(0, sizingRule!.indexOf("{"));
    expect(selectors).toContain(".livestream-container .xterm-scrollable-element");
    expect(selectors).toContain(".livestream-container .xterm-viewport");
    expect(selectors).toContain(".livestream-container .xterm-screen");
  });
});
