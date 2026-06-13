import { describe, expect, test, vi } from "vite-plus/test";
import {
  createClipboardKeyHandler,
  type ClipboardLike,
  type ClipboardTerminal,
} from "../terminalClipboard";

function makeTerminal(overrides: Partial<ClipboardTerminal> = {}): ClipboardTerminal {
  return {
    hasSelection: () => false,
    getSelection: () => "",
    paste: vi.fn(),
    ...overrides,
  };
}

function makeClipboard(readValue = ""): ClipboardLike & {
  writeText: ReturnType<typeof vi.fn>;
  readText: ReturnType<typeof vi.fn>;
} {
  return {
    writeText: vi.fn(() => Promise.resolve()),
    readText: vi.fn(() => Promise.resolve(readValue)),
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
    const clipboard = makeClipboard();
    const handler = createClipboardKeyHandler(term, clipboard);

    const handled = handler(keyEvent("c", { ctrlKey: true }));

    expect(handled).toBe(false);
    expect(clipboard.writeText).toHaveBeenCalledWith("hello");
  });

  test("Cmd+C copies the selection too (macOS)", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "world" });
    const clipboard = makeClipboard();
    const handler = createClipboardKeyHandler(term, clipboard);

    expect(handler(keyEvent("c", { metaKey: true }))).toBe(false);
    expect(clipboard.writeText).toHaveBeenCalledWith("world");
  });

  test("Ctrl+C without a selection falls through to the PTY as SIGINT", () => {
    const term = makeTerminal({ hasSelection: () => false });
    const clipboard = makeClipboard();
    const handler = createClipboardKeyHandler(term, clipboard);

    const handled = handler(keyEvent("c", { ctrlKey: true }));

    expect(handled).toBe(true);
    expect(clipboard.writeText).not.toHaveBeenCalled();
  });

  test("Ctrl+V pastes clipboard contents into the terminal", async () => {
    const paste = vi.fn();
    const term = makeTerminal({ paste });
    const clipboard = makeClipboard("pasted text");
    const handler = createClipboardKeyHandler(term, clipboard);

    const handled = handler(keyEvent("v", { ctrlKey: true }));

    expect(handled).toBe(false);
    expect(clipboard.readText).toHaveBeenCalled();
    // paste happens after the clipboard read resolves.
    await Promise.resolve();
    expect(paste).toHaveBeenCalledWith("pasted text");
  });

  test("Ctrl+V with an empty clipboard does not paste an empty string", async () => {
    const paste = vi.fn();
    const term = makeTerminal({ paste });
    const clipboard = makeClipboard("");
    const handler = createClipboardKeyHandler(term, clipboard);

    expect(handler(keyEvent("v", { ctrlKey: true }))).toBe(false);
    await Promise.resolve();
    expect(paste).not.toHaveBeenCalled();
  });

  test("plain keystrokes are forwarded untouched", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "sel" });
    const clipboard = makeClipboard("clip");
    const handler = createClipboardKeyHandler(term, clipboard);

    // 'c' without a modifier, and a modified-but-unrelated key.
    expect(handler(keyEvent("c"))).toBe(true);
    expect(handler(keyEvent("a", { ctrlKey: true }))).toBe(true);
    expect(clipboard.writeText).not.toHaveBeenCalled();
    expect(clipboard.readText).not.toHaveBeenCalled();
  });

  test("Alt-modified copy/paste combos are ignored", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "sel" });
    const clipboard = makeClipboard("clip");
    const handler = createClipboardKeyHandler(term, clipboard);

    expect(handler(keyEvent("c", { ctrlKey: true, altKey: true }))).toBe(true);
    expect(handler(keyEvent("v", { ctrlKey: true, altKey: true }))).toBe(true);
    expect(clipboard.writeText).not.toHaveBeenCalled();
    expect(clipboard.readText).not.toHaveBeenCalled();
  });

  test("only keydown is handled, so keypress does not double-fire", () => {
    const term = makeTerminal({ hasSelection: () => true, getSelection: () => "sel" });
    const clipboard = makeClipboard();
    const handler = createClipboardKeyHandler(term, clipboard);

    const handled = handler(
      keyEvent("c", { ctrlKey: true, type: "keypress" } as Partial<KeyboardEvent>),
    );

    expect(handled).toBe(true);
    expect(clipboard.writeText).not.toHaveBeenCalled();
  });
});
