/**
 * The slice of the xterm `Terminal` API the clipboard handler needs. Kept
 * narrow so the handler can be unit-tested without spinning up a real terminal.
 */
export interface ClipboardTerminal {
  hasSelection(): boolean;
  getSelection(): string;
}

/**
 * Copy text to the system clipboard.
 *
 * Prefers the async Clipboard API, but falls back to a hidden-textarea
 * `execCommand("copy")` when it is unavailable. That fallback matters because
 * `navigator.clipboard` only exists in secure contexts: a worktree QA preview
 * served over plain HTTP is *not* secure, so without it Ctrl+C copy would
 * silently do nothing there.
 */
export function copyTextToClipboard(text: string): void {
  const clip = navigator.clipboard as Clipboard | undefined;
  if (clip && typeof clip.writeText === "function") {
    void clip.writeText(text).catch(() => execCommandCopy(text));
    return;
  }
  execCommandCopy(text);
}

function execCommandCopy(text: string): void {
  const previouslyFocused = document.activeElement as HTMLElement | null;
  const textarea = document.createElement("textarea");
  textarea.value = text;
  // Keep the element out of view and out of the layout/focus flow.
  textarea.style.position = "fixed";
  textarea.style.top = "0";
  textarea.style.left = "0";
  textarea.style.opacity = "0";
  textarea.style.pointerEvents = "none";
  textarea.setAttribute("aria-hidden", "true");
  document.body.appendChild(textarea);
  textarea.select();
  try {
    document.execCommand("copy");
  } catch {
    /* clipboard genuinely unavailable — nothing more we can do */
  } finally {
    document.body.removeChild(textarea);
    // Restore focus to the terminal so typing continues uninterrupted.
    previouslyFocused?.focus?.();
  }
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
 * Returning `false` tells xterm to skip its own handling of the keystroke
 * *without* calling `preventDefault`, so the browser still performs its default
 * clipboard action. For paste that is exactly what we want: xterm's built-in
 * `paste` listener reads `event.clipboardData` (which works even in insecure
 * contexts), so we just suppress the literal `\x16` and let the native paste
 * through. Copy is done explicitly via {@link copyTextToClipboard} so it is
 * reliable regardless of whether the browser also fires a `copy` event.
 *
 * Pass the handler to `term.attachCustomKeyEventHandler(...)`.
 */
export function createClipboardKeyHandler(
  term: ClipboardTerminal,
  copyText: (text: string) => void = copyTextToClipboard,
): (event: KeyboardEvent) => boolean {
  return (event) => {
    // xterm forwards keydown and keypress; only act once, on keydown.
    if (event.type !== "keydown") return true;

    // Require the primary modifier and reject Alt-combos so we don't hijack
    // unrelated shortcuts.
    if (!(event.ctrlKey || event.metaKey) || event.altKey) return true;

    const key = event.key.toLowerCase();

    // Copy only when there is a selection; otherwise let Ctrl+C through as SIGINT.
    if (key === "c") {
      if (term.hasSelection()) {
        const selection = term.getSelection();
        if (selection) {
          copyText(selection);
          return false;
        }
      }
      return true;
    }

    // Let xterm's native paste handler run: returning false suppresses the
    // literal \x16 without preventing the browser's paste event.
    if (key === "v") {
      return false;
    }

    return true;
  };
}
