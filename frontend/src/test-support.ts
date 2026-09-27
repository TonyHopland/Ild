// Shared helpers for tests. Not a test file itself, so it is outside the
// `src/**/*.test.{ts,tsx}` include and never collected as a suite.
import { act, fireEvent } from "@testing-library/react";
import { vi } from "vite-plus/test";
import { workItemService } from "./services/auth";
import { WorkItemStatus } from "./types";
import type {
  ChatMessageAppendedPayload,
  ChatTurnCompletedPayload,
  ChatTurnProgressPayload,
  ChatTurnStartedPayload,
  WorkItem,
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

/** A board page request, as the Taskboard sends it for one status column. */
export interface FakeBoardPageQuery {
  status: string;
  search?: string;
  repositoryId?: string;
  tags?: string[];
  skip: number;
  take: number;
}

/** The board filter the per-status counts are taken under. */
export interface FakeBoardFilter {
  search?: string;
  repositoryId?: string;
  tags?: string[];
}

/** Newest first by creation time, then by id (code units, descending). */
export function compareFakeServerOrder(a: WorkItem, b: WorkItem): number {
  const byCreated = Date.parse(b.createdAt) - Date.parse(a.createdAt);
  if (byCreated !== 0) return byCreated;
  if (a.id === b.id) return 0;
  return a.id < b.id ? 1 : -1;
}

function matchesFakeFilter(item: WorkItem, filter: FakeBoardFilter): boolean {
  if (filter.repositoryId && item.repositoryId !== filter.repositoryId) return false;
  const wanted = (filter.tags ?? []).filter((t) => t.trim() !== "").map((t) => t.toLowerCase());
  const carried = (item.tags ?? []).map((t) => t.toLowerCase());
  if (!wanted.every((t) => carried.includes(t))) return false;
  const term = (filter.search ?? "").trim().toLowerCase();
  if (term) {
    const fields = [item.title, item.description, item.id];
    if (!fields.some((f) => typeof f === "string" && f.toLowerCase().includes(term))) return false;
  }
  return true;
}

/**
 * An in-memory stand-in for the board's listing endpoints (`/workitems/page`,
 * `/workitems/counts`, `/workitems/tags`) with the server's semantics: status,
 * repository, case-insensitive per-field search and every-tag filtering, newest
 * first with the id as tiebreaker, and totals that ignore skip/take. `items` is
 * the server's state; a test changes it to model changes made elsewhere.
 * `getAll` is stubbed too, because the detail dialog's dependency picker uses it.
 */
export function mockTaskboardServer(initial: WorkItem[]) {
  const items = [...initial];
  const page = (q: FakeBoardPageQuery) => {
    const matching = items
      .filter((wi) => wi.status === q.status && matchesFakeFilter(wi, q))
      .sort(compareFakeServerOrder);
    return { items: matching.slice(q.skip, q.skip + q.take), total: matching.length };
  };
  const counts = (filter: FakeBoardFilter) => {
    const result: Record<string, number> = {};
    for (const status of Object.values(WorkItemStatus)) {
      result[status] = items.filter(
        (wi) => wi.status === status && matchesFakeFilter(wi, filter),
      ).length;
    }
    return result;
  };
  const tags = () =>
    [...new Set(items.flatMap((wi) => wi.tags ?? []))].sort((a, b) =>
      a.toLowerCase().localeCompare(b.toLowerCase()),
    );
  const getPage = vi
    .spyOn(workItemService, "getPage")
    .mockImplementation(async (q: FakeBoardPageQuery) => page(q));
  const getCounts = vi
    .spyOn(workItemService, "getCounts")
    .mockImplementation(async (filter: FakeBoardFilter) => counts(filter));
  const getTags = vi.spyOn(workItemService, "getTags").mockImplementation(async () => tags());
  const getAll = vi.spyOn(workItemService, "getAll").mockResolvedValue([]);
  return { items, page, counts, tags, getPage, getCounts, getTags, getAll };
}
