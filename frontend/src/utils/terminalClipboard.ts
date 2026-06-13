/**
 * The slice of the xterm `Terminal` API the clipboard handler needs. Kept
 * narrow so the handler can be unit-tested without spinning up a real terminal.
 */
export interface ClipboardTerminal {
  hasSelection(): boolean;
  getSelection(): string;
  paste(data: string): void;
}

/** The clipboard surface used for copy/paste — a subset of `navigator.clipboard`. */
export interface ClipboardLike {
  writeText(data: string): Promise<void>;
  readText(): Promise<string>;
}

/**
 * Build a custom key event handler for xterm that maps the familiar Ctrl/Cmd+C
 * and Ctrl/Cmd+V shortcuts onto terminal copy/paste, matching the behaviour of
 * editor-integrated terminals (e.g. VS Code):
 *
 * - Ctrl/Cmd+C copies the current selection — but only when something is
 *   selected, so an empty Ctrl+C still reaches the shell as SIGINT.
 * - Ctrl/Cmd+V pastes the clipboard contents into the terminal.
 *
 * Returning `false` tells xterm to swallow the event (don't forward the
 * keystroke to the PTY); returning `true` lets it through unchanged.
 *
 * Pass the handler to `term.attachCustomKeyEventHandler(...)`.
 */
export function createClipboardKeyHandler(
  term: ClipboardTerminal,
  clipboard: ClipboardLike,
): (event: KeyboardEvent) => boolean {
  return (event) => {
    // xterm forwards keydown and keypress; only act once, on keydown.
    if (event.type !== "keydown") return true;

    // Require the primary modifier and reject Alt-combos so we don't hijack
    // unrelated shortcuts.
    if (!(event.ctrlKey || event.metaKey) || event.altKey) return true;

    const key = event.key.toLowerCase();

    // Copy only when there is a selection; otherwise let Ctrl+C through as SIGINT.
    if (key === "c" && term.hasSelection()) {
      const selection = term.getSelection();
      if (selection) {
        void clipboard.writeText(selection);
        return false;
      }
      return true;
    }

    // Paste clipboard contents into the terminal.
    if (key === "v") {
      void clipboard.readText().then((text) => {
        if (text) term.paste(text);
      });
      return false;
    }

    return true;
  };
}
