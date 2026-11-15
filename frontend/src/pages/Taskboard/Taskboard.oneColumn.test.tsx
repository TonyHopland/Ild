import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import Taskboard from "./index";
import { foldedColumnKey } from "../../hooks/useFoldedColumns";
import { seedPreferences } from "../../utils/userPreferences";
import { deferred, mockTaskboardServer, signalRHub, flushPromises } from "../../test-support";
import { WorkItemStatus, WorkItem } from "../../types";
import * as authServices from "../../services/auth";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

vi.mock("../../hooks/useSignalR", async () =>
  (await import("../../test-support")).fakeSignalRModule(),
);

afterEach(() => {
  vi.useRealTimers();
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

beforeEach(() => {
  signalRHub.reset();
  localStorage.clear();
  seedPreferences({ [foldedColumnKey(WorkItemStatus.Done)]: "false" });
});

function item(id: string, status: WorkItemStatus, n: number): WorkItem {
  return {
    id,
    title: id,
    description: "",
    status,
    priority: -n,
    tags: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: status === WorkItemStatus.HumanFeedback ? "Out of Memory" : null,
    humanFeedbackActions: null,
    createdAt: new Date(Date.UTC(2026, 0, 1) + n * 3_600_000).toISOString(),
    startedAt: null,
    completedAt: null,
    currentLoopRunId: "run-1",
    dependencyIds: [],
    dependentIds: [],
  };
}

function renderBoard() {
  return render(
    <MemoryRouter initialEntries={["/taskboard"]}>
      <Routes>
        <Route path="/taskboard" element={<Taskboard />} />
        <Route path="/taskboard/:workItemId" element={<Taskboard />} />
      </Routes>
    </MemoryRouter>,
    { wrapper: WorkItemHubProvider },
  );
}

/** The columns that show a card titled `title`. */
function columnsHolding(title: string): string[] {
  return Array.from(document.querySelectorAll<HTMLElement>(".taskboard-column"))
    .filter((column) =>
      Array.from(column.querySelectorAll(".work-item-card")).some(
        (card) => card.getAttribute("aria-label")!.split(", status ")[0] === title,
      ),
    )
    .map((column) => column.querySelector(".taskboard-column-title")?.textContent ?? "?");
}

function mulberry32(seed: number) {
  let a = seed;
  return () => {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

interface HeldRead {
  label: string;
  release: () => void;
}

async function settle(ms = 0) {
  await act(async () => {
    if (ms) await vi.advanceTimersByTimeAsync(ms);
  });
  await flushPromises();
}

const OUT_OF_MEMORY = "Out of Memory";

function board() {
  const server = mockTaskboardServer([
    item("wi-1", WorkItemStatus.Running, 1),
    item("wi-2", WorkItemStatus.Running, 2),
    item("wi-3", WorkItemStatus.HumanFeedback, 3),
  ]);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.settingsService, "get").mockResolvedValue({
    key: "scheduler.isPaused",
    value: "false",
  });
  const setStatus = (status: WorkItemStatus) => {
    server.items[0].status = status;
    server.items[0].humanFeedbackReason =
      status === WorkItemStatus.HumanFeedback ? OUT_OF_MEMORY : null;
  };
  return { server, setStatus };
}

/** Every read the server answers by hand: its answer is what the server holds when it is released, or when it was asked. */
function holdReads(
  server: ReturnType<typeof board>["server"],
  held: HeldRead[],
  random: () => number,
) {
  const hold = <T,>(label: string, read: () => T): Promise<T> => {
    const asked = read();
    const askedFirst = random() < 0.5;
    return new Promise<T>((resolve) => {
      held.push({ label, release: () => resolve(askedFirst ? asked : read()) });
    });
  };
  vi.spyOn(authServices.workItemService, "getById").mockImplementation((id: string) =>
    hold("getById", () => ({ ...server.items.find((wi) => wi.id === id)! })),
  );
  const answer = server.getPage.getMockImplementation()!;
  server.getPage.mockImplementation((query) =>
    random() < 0.5 ? answer(query) : hold("page", () => ({ ...server.page(query) })),
  );
}

describe("a work item's card is in one column", () => {
  test("a copy read before a failure that lands after the card moved to Needs you does not leave it in Running too", async () => {
    vi.useFakeTimers();
    const { server, setStatus } = board();
    renderBoard();
    await settle();
    expect(columnsHolding("wi-1")).toEqual(["Running"]);

    // The failure's event moves the card; the copy the card's own re-read returns predates the failure.
    const stale = deferred<WorkItem>();
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(async (id: string) => {
      const found = server.items.find((wi) => wi.id === id)!;
      return found.status === WorkItemStatus.Running ? stale.promise : { ...found };
    });
    await signalRHub.emit("HumanFeedbackRequired", { workItemId: "wi-1", reason: OUT_OF_MEMORY });
    expect(columnsHolding("wi-1")).toEqual(["Needs you"]);
    stale.resolve({ ...server.items[0] });
    setStatus(WorkItemStatus.HumanFeedback);
    await settle();

    expect(columnsHolding("wi-1")).toEqual(["Needs you"]);
    await settle(600);
    expect(columnsHolding("wi-1")).toEqual(["Needs you"]);
  });

  test.each(Array.from({ length: 60 }, (_, n) => n + 1))(
    "after a node fails, is resumed and fails again, whatever order the events and reads arrive in (seed %i)",
    { timeout: 60_000 },
    async (seed) => {
      const random = mulberry32(seed);
      const pick = <T,>(xs: T[]): T => xs[Math.floor(random() * xs.length)];
      const trace: string[] = [];
      vi.useFakeTimers();
      const { server, setStatus } = board();
      const held: HeldRead[] = [];
      holdReads(server, held, random);

      renderBoard();
      await settle();
      while (held.length) held.shift()!.release();
      await settle();

      const check = () => {
        const columns = columnsHolding("wi-1");
        trace.push(columns.join(" + ") || "none");
        expect(columns.length, trace.join("\n")).toBeLessThanOrEqual(1);
      };

      const eventsOf = (status: WorkItemStatus) => {
        const events = [
          () =>
            signalRHub.emit("WorkItemStateChanged", {
              workItemId: "wi-1",
              oldStatus: "Running",
              newStatus: status,
            }),
          () => signalRHub.emit("WorkItemRunProgressed", { workItemId: "wi-1" }),
        ];
        if (status === WorkItemStatus.HumanFeedback)
          events.push(() =>
            signalRHub.emit("HumanFeedbackRequired", { workItemId: "wi-1", reason: OUT_OF_MEMORY }),
          );
        return events;
      };

      const transitions = [
        WorkItemStatus.HumanFeedback,
        WorkItemStatus.Running,
        WorkItemStatus.HumanFeedback,
      ];
      const undelivered: (() => Promise<void>)[] = [];
      let next = 0;
      while (next < transitions.length || undelivered.length) {
        const moves = [
          ...(next < transitions.length ? ["server"] : []),
          ...(undelivered.length ? ["event", "event"] : []),
          ...(held.length ? ["release", "release"] : []),
          "time",
        ];
        const move = pick(moves);
        trace.push(move);
        if (move === "server") {
          setStatus(transitions[next]);
          undelivered.push(...eventsOf(transitions[next++]));
        } else if (move === "event") {
          await undelivered.splice(Math.floor(random() * undelivered.length), 1)[0]();
        } else if (move === "release") {
          const [read] = held.splice(Math.floor(random() * held.length), 1);
          trace.push(read.label);
          read.release();
          await settle();
        } else {
          await settle(pick([100, 500, 600]));
        }
        check();
      }
      while (held.length) {
        held.splice(Math.floor(random() * held.length), 1)[0].release();
        await settle();
        check();
      }
      await settle(600);
      while (held.length) {
        held.shift()!.release();
        await settle();
      }
      await settle(600);

      expect(columnsHolding("wi-1"), trace.join("\n")).toEqual(["Needs you"]);
    },
  );
});
