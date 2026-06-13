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
});
