import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import {
  copyTextToClipboard,
  createClipboardKeyHandler,
  type ClipboardTerminal,
} from "../terminalClipboard";

function makeTerminal(overrides: Partial<ClipboardTerminal> = {}): ClipboardTerminal {
  return {
    hasSelection: () => false,
    getSelection: () => "",
    ...overrides,
  };
}

function keyEvent(key: string, init: Partial<KeyboardEvent> = {}): KeyboardEvent {
  return {
    type: "keydown",
    key,
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    ...init,
  } as KeyboardEvent;
}

describe("createClipboardKeyHandler", () => {
  test("Ctrl+C copies the selection and swallows the event", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "hello" });
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    const handled = handler(keyEvent("c", { ctrlKey: true }));

    // false => xterm skips its own handling (no SIGINT) without preventDefault.
    expect(handled).toBe(false);
    expect(copyText).toHaveBeenCalledWith("hello");
  });

  test("Cmd+C copies the selection too (macOS)", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "world" });
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    expect(handler(keyEvent("c", { metaKey: true }))).toBe(false);
    expect(copyText).toHaveBeenCalledWith("world");
  });

  test("Ctrl+C without a selection falls through to the PTY as SIGINT", () => {
    const term = makeTerminal({ hasSelection: () => false });
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    const handled = handler(keyEvent("c", { ctrlKey: true }));

    expect(handled).toBe(true);
    expect(copyText).not.toHaveBeenCalled();
  });

  test("Ctrl+C with an empty selection string is treated as SIGINT", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "" });
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    expect(handler(keyEvent("c", { ctrlKey: true }))).toBe(true);
    expect(copyText).not.toHaveBeenCalled();
  });

  test("Ctrl+V suppresses the literal \\x16 so the native paste event can run", () => {
    const term = makeTerminal();
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    // Returning false makes xterm skip emitting \x16 without preventDefault,
    // leaving the browser's native paste (which xterm handles) intact.
    expect(handler(keyEvent("v", { ctrlKey: true }))).toBe(false);
    expect(handler(keyEvent("v", { metaKey: true }))).toBe(false);
    expect(copyText).not.toHaveBeenCalled();
  });

  test("plain keystrokes are forwarded untouched", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "sel" });
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    // 'c'/'v' without a modifier, and a modified-but-unrelated key.
    expect(handler(keyEvent("c"))).toBe(true);
    expect(handler(keyEvent("v"))).toBe(true);
    expect(handler(keyEvent("a", { ctrlKey: true }))).toBe(true);
    expect(copyText).not.toHaveBeenCalled();
  });

  test("Alt-modified copy/paste combos are ignored", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "sel" });
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    expect(handler(keyEvent("c", { ctrlKey: true, altKey: true }))).toBe(true);
    expect(handler(keyEvent("v", { ctrlKey: true, altKey: true }))).toBe(true);
    expect(copyText).not.toHaveBeenCalled();
  });

  test("only keydown is handled, so keypress does not double-fire", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "sel" });
    const copyText = vi.fn();
    const handler = createClipboardKeyHandler(term, copyText);

    const handled = handler(
      keyEvent("c", { ctrlKey: true, type: "keypress" } as Partial<KeyboardEvent>),
    );

    expect(handled).toBe(true);
    expect(copyText).not.toHaveBeenCalled();
  });
});

describe("copyTextToClipboard", () => {
  function setClipboard(value: Clipboard | undefined): void {
    Object.defineProperty(navigator, "clipboard", { value, configurable: true });
  }

  afterEach(() => {
    setClipboard(undefined);
    delete (document as { execCommand?: unknown }).execCommand;
  });

  test("uses the async Clipboard API when it is available (secure context)", () => {
    const writeText = vi.fn(() => Promise.resolve());
    setClipboard({ writeText } as unknown as Clipboard);
    const execCommand = vi.fn(() => true);
    (document as { execCommand: unknown }).execCommand = execCommand;

    copyTextToClipboard("secure text");

    expect(writeText).toHaveBeenCalledWith("secure text");
    expect(execCommand).not.toHaveBeenCalled();
  });

  test("falls back to execCommand when navigator.clipboard is absent (insecure context)", () => {
    setClipboard(undefined);
    const execCommand = vi.fn(() => true);
    (document as { execCommand: unknown }).execCommand = execCommand;

    copyTextToClipboard("insecure text");

    expect(execCommand).toHaveBeenCalledWith("copy");
    // The temporary textarea must be cleaned up afterwards.
    expect(document.querySelector("textarea")).toBeNull();
  });

  test("does not throw when no clipboard mechanism works at all", () => {
    setClipboard(undefined);
    // No execCommand defined and none assigned — the deepest fallback.
    expect(() => copyTextToClipboard("nowhere")).not.toThrow();
    expect(document.querySelector("textarea")).toBeNull();
  });
});
