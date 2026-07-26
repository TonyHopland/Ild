// Shared helpers for tests. Not a test file itself, so it is outside the
// `src/**/*.test.{ts,tsx}` include and never collected as a suite.
import { fireEvent, waitFor } from "@testing-library/react";

/**
 * Presses Escape on the document until `settled` passes.
 *
 * Dialogs close from a document-level keydown listener that React attaches in a
 * passive effect, and passive effects run on React's own scheduler — nothing in
 * `render` or `waitFor` guarantees that flush has happened by the time the next
 * statement runs. So a dialog can be in the DOM while its listener is not yet
 * attached, and a keydown has no queue: a press that lands in that window is
 * swallowed for good, leaving the test to wait out its timeout on a close that
 * will never come. Raising the timeout cannot help, because the lost press is
 * never redelivered.
 *
 * Pressing inside the retry loop removes the ordering dependency — a swallowed
 * press just costs one more poll. `settled` is checked immediately after each
 * press, so the loop stops on the first press that takes effect. That matters
 * where a second effective press would undo the first: a Discard confirm has its
 * own Escape-to-cancel listener.
 */
export async function pressEscapeUntil(settled: () => void): Promise<void> {
  await waitFor(() => {
    fireEvent.keyDown(document, { key: "Escape" });
    settled();
  });
}
