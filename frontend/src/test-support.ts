// Shared helpers for tests. Not a test file itself, so it is outside the
// `src/**/*.test.{ts,tsx}` include and never collected as a suite.
import { act, fireEvent } from "@testing-library/react";
import type {
  ChatMessageAppendedPayload,
  ChatTurnCompletedPayload,
  ChatTurnProgressPayload,
  ChatTurnStartedPayload,
} from "./types";

/** Presses before the loop gives up and reports the caller's own assertion. */
const MaxPresses = 40;

/** Yield between presses, long enough for React to run a passive effect. */
const YieldMs = 10;

/**
 * Presses Escape on the document until `settled` passes.
 *
 * Dialogs close from a document-level keydown listener that React attaches in a
 * passive effect, and passive effects run on React's own scheduler — nothing in
 * `render` guarantees that flush has happened by the time the next statement
 * runs. So a dialog can be in the DOM while its listener is not yet attached,
 * and a keydown has no queue: a press that lands in that window is swallowed
 * for good, leaving the test to wait out its timeout on a close that will never
 * come.
 *
 * Pressing inside the retry loop removes the ordering dependency — a swallowed
 * press just costs one more attempt. `settled` is checked immediately after
 * each press, so the loop stops on the first press that takes effect. That
 * matters where a second effective press would undo the first: a Discard
 * confirm has its own Escape-to-cancel listener.
 *
 * The budget is counted in PRESSES, not in wall-clock time, which is why this
 * does not use `waitFor`. Under `waitFor` the whole loop shares one 1s deadline,
 * and a suite this size does get stalled off the CPU for longer than that: one
 * stall is then enough to spend the entire budget, and the loop gives up having
 * pressed once or twice, however many presses the dialog was waiting for.
 * Measured on this machine — a single 2.5s stall leaves `waitFor` settling
 * after 2 presses and never reaching the 4 the swallow fixture needs, which is
 * the failure the gate hit. Counting presses has no deadline to spend, so a
 * stalled run is slow rather than red.
 */
export async function pressEscapeUntil(settled: () => void): Promise<void> {
  for (let press = 1; press < MaxPresses; press++) {
    fireEvent.keyDown(document, { key: "Escape" });
    try {
      settled();
      return;
    } catch {
      // Swallowed, or the state `settled` waits on has not rendered yet.
    }
    // Inside act so React's pending passive effects — including the one that
    // attaches the listener this press may have missed — run before the next.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, YieldMs));
    });
  }

  // Out of presses: press once more and let `settled` throw, so a dialog that
  // never closes fails with the caller's own assertion rather than a timeout.
  fireEvent.keyDown(document, { key: "Escape" });
  settled();
}

/**
 * The chat hub events a bubble test simulates, each mapped to the payload the
 * server really sends (`SignalRPayloads.cs`). A test's `emit` helper is typed
 * against this, so an event built without its turn id is a compile error rather
 * than a handler call that quietly does nothing: every one of these carries a
 * turn id, and the bubble matches on it to tell a replaced turn's traffic from
 * its successor's — an untagged event is silently ignored, which is exactly how
 * a test can go on passing while exercising none of what it names.
 */
export interface ChatHubEvents {
  ChatTurnStarted: ChatTurnStartedPayload;
  ChatTurnProgress: ChatTurnProgressPayload;
  ChatMessageAppended: ChatMessageAppendedPayload;
  ChatTurnCompleted: ChatTurnCompletedPayload;
}
