import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import {
  act,
  render,
  screen,
  cleanup,
  createEvent,
  fireEvent,
  waitFor,
} from "@testing-library/react";
import { Profiler, useState, type ComponentProps } from "react";
import BoardColumn from "./TaskboardColumn";

type ColumnProps = ComponentProps<typeof BoardColumn>;

/** The column as the board drives it, holding the fold choice the board would keep. */
function TaskboardColumn(
  props: Omit<ColumnProps, "foldChoice" | "onFoldChange"> & Partial<ColumnProps>,
) {
  const [choice, setChoice] = useState<boolean | null>(props.foldChoice ?? null);
  return (
    <BoardColumn
      {...props}
      foldChoice={choice}
      onFoldChange={(folded) => {
        setChoice(folded);
        props.onFoldChange?.(folded);
      }}
    />
  );
}
import WorkItemCard from "./WorkItemCard";
import { WorkItem, WorkItemStatus } from "../types";
import * as authServices from "../services/auth";

afterEach(() => {
  cleanup();
  // Service spies are per-test; without this a spy's calls leak into the next
  // test that spies on the same method.
  vi.restoreAllMocks();
});

function makeItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "1",
    title: "Item A",
    description: "desc",
    status: WorkItemStatus.Backlog,
    priority: 0,
    tags: [],
    loopTemplateId: "",
    loopTemplateVersion: "",
    repositoryId: "",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: new Date().toISOString(),
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

describe("TaskboardColumn", () => {
  test("the count badge shows the server total, not the number of cards loaded", () => {
    const items = [makeItem({ id: "1" }), makeItem({ id: "2", title: "Item B" })];
    const { container } = render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={items}
        total={57}
        onWorkItemUpdate={() => {}}
      />,
    );

    expect(screen.getByText("Backlog")).toBeTruthy();
    expect(container.querySelector(".taskboard-column-count")?.textContent).toBe("57");
    expect(screen.getByText("Item A")).toBeTruthy();
    expect(screen.getByText("Item B")).toBeTruthy();
  });

  test("renders zero count when there are no items", () => {
    const { container } = render(
      <TaskboardColumn
        status={WorkItemStatus.Done}
        label="Done"
        workItems={[]}
        total={0}
        onWorkItemUpdate={() => {}}
      />,
    );

    expect(screen.getByText("Done")).toBeTruthy();
    expect(container.querySelector(".taskboard-column-count")?.textContent).toBe("0");
  });

  // WI-203 "PR disappears" is observed on the Done column: an item that had a
  // PR badge in Human Feedback loses it the moment it is dragged across. The
  // column has to keep surfacing the PR history its cards carry.
  test("Done column cards keep showing their PR history", () => {
    const { container } = render(
      <TaskboardColumn
        // Done starts folded; this one has been opened.
        foldChoice={false}
        status={WorkItemStatus.Done}
        label="Done"
        workItems={[
          makeItem({
            id: "1",
            status: WorkItemStatus.Done,
            prUrl: null,
            pullRequests: [
              { url: "https://forgejo.example.com/repo/pulls/11", runId: "run-2", merged: true },
              { url: "https://forgejo.example.com/repo/pulls/10", runId: "run-1", merged: false },
            ],
          }),
        ]}
        total={1}
        onWorkItemUpdate={() => {}}
      />,
    );

    expect(container.querySelector(".work-item-pr-history")?.textContent).toContain("2");
  });

  test("renders every card it is given, with no paging of its own", () => {
    const items = Array.from({ length: 21 }, (_, i) =>
      makeItem({ id: String(i), title: `Item ${i}` }),
    );
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={items}
        total={30}
        onWorkItemUpdate={() => {}}
        onLoadMore={() => {}}
      />,
    );

    for (let i = 0; i < 21; i++) {
      expect(screen.getByText(`Item ${i}`)).toBeTruthy();
    }
  });

  test("offers Load more while fewer cards are loaded than the total and asks for the next page", () => {
    const items = Array.from({ length: 20 }, (_, i) =>
      makeItem({ id: String(i), title: `Item ${i}` }),
    );
    const onLoadMore = vi.fn();
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={items}
        total={21}
        onWorkItemUpdate={() => {}}
        onLoadMore={onLoadMore}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Load more" }));

    expect(onLoadMore).toHaveBeenCalledTimes(1);
  });

  test("disables Load more while a page is being fetched", () => {
    const items = Array.from({ length: 20 }, (_, i) =>
      makeItem({ id: String(i), title: `Item ${i}` }),
    );
    const onLoadMore = vi.fn();
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={items}
        total={45}
        onWorkItemUpdate={() => {}}
        onLoadMore={onLoadMore}
        loadingMore
      />,
    );

    const loadMore = screen.getByRole("button", { name: /load/i }) as HTMLButtonElement;
    expect(loadMore.disabled).toBe(true);
    fireEvent.click(loadMore);
    expect(onLoadMore).not.toHaveBeenCalled();
  });

  // Moving a Ready item that never started back to Backlog is a legal reset for
  // re-planning. The board is not the thing blocking it — it posts the move like
  // any other — so this pins the request the column already sends and guards the
  // path while the API side is fixed.
  test("dropping a Ready work item into the Backlog column transitions it to Backlog", async () => {
    const transitionSpy = vi
      .spyOn(authServices.workItemService, "transition")
      .mockResolvedValue(undefined as unknown as void);
    const moved = makeItem({ id: "1", status: WorkItemStatus.Backlog });
    vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(moved);

    const onWorkItemUpdate = vi.fn();
    const onError = vi.fn();
    // The dragged card lives in the Ready column, so the Backlog column it is
    // dropped on does not hold it.
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={[]}
        total={0}
        onWorkItemUpdate={onWorkItemUpdate}
        onError={onError}
      />,
    );

    const column = screen.getByText("Backlog").closest("div");
    fireEvent.drop(column!, {
      dataTransfer: { getData: () => "1" },
    } as unknown as DragEvent);

    await waitFor(() => expect(transitionSpy).toHaveBeenCalledWith("1", WorkItemStatus.Backlog));
    await waitFor(() => expect(onWorkItemUpdate).toHaveBeenCalledWith(moved));
    expect(onError).not.toHaveBeenCalled();
  });

  test("does not call transition when dropping work item into the same column", () => {
    const transitionSpy = vi
      .spyOn(authServices.workItemService, "transition")
      .mockResolvedValue(undefined as unknown as void);

    const item = makeItem({ id: "1", status: WorkItemStatus.Backlog });
    render(
      <TaskboardColumn
        status={WorkItemStatus.Backlog}
        label="Backlog"
        workItems={[item]}
        total={1}
        onWorkItemUpdate={() => {}}
      />,
    );

    const column = screen.getByText("Backlog").closest("div");
    expect(column).toBeTruthy();

    // Simulate dropping the same work item into its own column
    fireEvent.drop(column!, {
      dataTransfer: { getData: () => "1" },
    } as unknown as DragEvent);

    // transition should NOT have been called
    expect(transitionSpy).not.toHaveBeenCalled();
  });
});

const CARD_HEIGHT = 100;

/**
 * Lays the cards out one under the other, CARD_HEIGHT each, in the given order
 * (jsdom has no layout). An element holding exactly one of the cards measures as
 * that card; anything holding several, such as the column, spans them all and
 * room below.
 */
function layOutCards(ids: string[]) {
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
    const text = this.textContent ?? "";
    const held = ids.filter((id) => text.includes(`#${id}`));
    const top = held.length === 1 ? ids.indexOf(held[0]) * CARD_HEIGHT : 0;
    const height = held.length === 1 ? CARD_HEIGHT : (ids.length + 2) * CARD_HEIGHT;
    return {
      x: 0,
      y: top,
      top,
      left: 0,
      right: 200,
      bottom: top + height,
      width: 200,
      height,
      toJSON: () => ({}),
    } as DOMRect;
  });
}

function card(id: string): HTMLElement {
  return screen.getByRole("button", { name: new RegExp(`^Item ${id},`) });
}

/** Drags the work item with `draggedId` over `target` and drops it at height `clientY`. */
function dropAt(target: Element, draggedId: string, clientY: number) {
  const dataTransfer = { getData: () => draggedId, setData: () => {}, dropEffect: "move" };
  for (const fire of [createEvent.dragEnter, createEvent.dragOver, createEvent.drop]) {
    const event = fire(target, { dataTransfer });
    Object.defineProperty(event, "clientY", { value: clientY });
    Object.defineProperty(event, "pageY", { value: clientY });
    fireEvent(target, event);
  }
}

/** Lets the drop's requests and their callbacks run. */
async function settle() {
  for (let i = 0; i < 5; i++) await Promise.resolve();
  await new Promise((r) => setTimeout(r, 0));
}

function itemsIn(status: WorkItemStatus, ids: string[]): WorkItem[] {
  return ids.map((id) => makeItem({ id, title: `Item ${id}`, status }));
}

describe("TaskboardColumn drag and drop order", () => {
  function renderColumn(status: WorkItemStatus, ids: string[]) {
    const onWorkItemUpdate = vi.fn();
    const onError = vi.fn();
    render(
      <TaskboardColumn
        status={status}
        label={status}
        workItems={itemsIn(status, ids)}
        total={ids.length}
        onWorkItemUpdate={onWorkItemUpdate}
        onError={onError}
        foldChoice={false}
      />,
    );
    layOutCards(ids);
    return { onWorkItemUpdate, onError };
  }

  function spyRequests() {
    const transition = vi
      .spyOn(authServices.workItemService, "transition")
      .mockResolvedValue(undefined as unknown as void);
    vi.spyOn(authServices.workItemService, "getById").mockImplementation(async (id: string) =>
      makeItem({ id, title: `Item ${id}`, status: WorkItemStatus.Ready }),
    );
    const reorder = vi
      .spyOn(authServices.workItemService, "reorder")
      .mockImplementation(async (id: string) =>
        makeItem({ id, title: `Item ${id}`, status: WorkItemStatus.Backlog, priority: -42 }),
      );
    return { transition, reorder };
  }

  test("dropping on the upper half of a card places the dragged card directly above it", async () => {
    const { transition, reorder } = spyRequests();
    const { onWorkItemUpdate, onError } = renderColumn(WorkItemStatus.Backlog, [
      "a",
      "b",
      "c",
      "d",
    ]);

    dropAt(card("b"), "d", CARD_HEIGHT + 20);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(reorder.mock.calls[0][0]).toBe("d");
    expect(reorder.mock.calls[0][1]).toEqual(
      expect.objectContaining({ aboveId: "a", belowId: "b" }),
    );
    expect(transition).not.toHaveBeenCalled();
    await waitFor(() =>
      expect(onWorkItemUpdate).toHaveBeenLastCalledWith(
        expect.objectContaining({ id: "d", priority: -42 }),
      ),
    );
    expect(onError).not.toHaveBeenCalled();
  });

  test("dropping on the lower half of a card places the dragged card directly below it", async () => {
    const { transition, reorder } = spyRequests();
    renderColumn(WorkItemStatus.Backlog, ["a", "b", "c", "d"]);

    dropAt(card("c"), "a", 2 * CARD_HEIGHT + 80);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(reorder.mock.calls[0][0]).toBe("a");
    expect(reorder.mock.calls[0][1]).toEqual(
      expect.objectContaining({ aboveId: "c", belowId: "d" }),
    );
    expect(transition).not.toHaveBeenCalled();
  });

  test("dropping on the top card's upper half makes the dragged card the top one", async () => {
    const { reorder } = spyRequests();
    renderColumn(WorkItemStatus.WorkQueue, ["a", "b", "c"]);

    dropAt(card("a"), "c", 10);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(reorder.mock.calls[0][0]).toBe("c");
    expect(reorder.mock.calls[0][1]).toEqual(expect.objectContaining({ belowId: "a" }));
    expect(reorder.mock.calls[0][1]?.aboveId).toBeUndefined();
  });

  test("dropping in the column outside the cards places the dragged card below the last one", async () => {
    const { reorder } = spyRequests();
    renderColumn(WorkItemStatus.Backlog, ["a", "b", "c"]);

    const column = screen.getByText(WorkItemStatus.Backlog).closest("div")!;
    dropAt(column, "a", 3 * CARD_HEIGHT + 50);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(reorder.mock.calls[0][0]).toBe("a");
    expect(reorder.mock.calls[0][1]).toEqual(expect.objectContaining({ aboveId: "c" }));
    expect(reorder.mock.calls[0][1]?.belowId).toBeUndefined();
  });

  test.each([
    ["the lower half of the card above it", "a", 80],
    ["the upper half of the card below it", "c", 2 * CARD_HEIGHT + 20],
    ["its own card", "b", CARD_HEIGHT + 50],
  ])("dropping a card on %s sends nothing", async (_where, targetId, clientY) => {
    const { transition, reorder } = spyRequests();
    const { onWorkItemUpdate } = renderColumn(WorkItemStatus.Backlog, ["a", "b", "c"]);

    dropAt(card(targetId), "b", clientY);
    await settle();

    expect(reorder).not.toHaveBeenCalled();
    expect(transition).not.toHaveBeenCalled();
    expect(onWorkItemUpdate).not.toHaveBeenCalled();
  });

  test("dropping the last card below the last card sends nothing", async () => {
    const { transition, reorder } = spyRequests();
    renderColumn(WorkItemStatus.Backlog, ["a", "b", "c"]);

    const column = screen.getByText(WorkItemStatus.Backlog).closest("div")!;
    dropAt(column, "c", 3 * CARD_HEIGHT + 50);
    await settle();

    expect(reorder).not.toHaveBeenCalled();
    expect(transition).not.toHaveBeenCalled();
  });

  test.each([
    [
      "a Work queue card between a Ready and a Work queue card",
      "a",
      "c",
      2 * CARD_HEIGHT + 30,
      { aboveId: "b", belowId: "c" },
    ],
    ["a Ready card above a Work queue card", "b", "a", 10, { belowId: "a" }],
    [
      "a Ready card between a Work queue and a Ready card",
      "d",
      "b",
      CARD_HEIGHT + 30,
      { aboveId: "a", belowId: "b" },
    ],
  ])(
    "dropping %s in the Work queue column only reorders it",
    async (_what, draggedId, targetId, clientY, neighbours) => {
      const { transition, reorder } = spyRequests();
      const statuses: Record<string, WorkItemStatus> = {
        a: WorkItemStatus.WorkQueue,
        b: WorkItemStatus.Ready,
        c: WorkItemStatus.WorkQueue,
        d: WorkItemStatus.Ready,
      };
      const ids = ["a", "b", "c", "d"];
      render(
        <TaskboardColumn
          status={WorkItemStatus.WorkQueue}
          label="Work queue"
          workItems={ids.map((id) => makeItem({ id, title: `Item ${id}`, status: statuses[id] }))}
          total={ids.length}
          onWorkItemUpdate={vi.fn()}
          onError={vi.fn()}
          foldChoice={false}
        />,
      );
      layOutCards(ids);

      dropAt(card(targetId), draggedId, clientY);

      await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
      expect(reorder.mock.calls[0][0]).toBe(draggedId);
      expect(reorder.mock.calls[0][1]).toEqual(neighbours);
      expect(transition).not.toHaveBeenCalled();
    },
  );

  test("a card from another column is moved and placed exactly where it was dropped in one request", async () => {
    const { transition, reorder } = spyRequests();
    const { onWorkItemUpdate, onError } = renderColumn(WorkItemStatus.WorkQueue, ["e", "f", "g"]);

    dropAt(card("f"), "x", CARD_HEIGHT + 30);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(transition).not.toHaveBeenCalled();
    expect(reorder.mock.calls[0][0]).toBe("x");
    expect(reorder.mock.calls[0][1]).toEqual({
      aboveId: "e",
      belowId: "f",
      targetStatus: WorkItemStatus.WorkQueue,
    });
    await waitFor(() =>
      expect(onWorkItemUpdate).toHaveBeenLastCalledWith(
        expect.objectContaining({ id: "x", priority: -42 }),
      ),
    );
    expect(onError).not.toHaveBeenCalled();
  });

  test("a card from another column dropped on the lower half of the last card lands at the bottom", async () => {
    const { transition, reorder } = spyRequests();
    renderColumn(WorkItemStatus.WorkQueue, ["e", "f"]);

    dropAt(card("f"), "x", CARD_HEIGHT + 70);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(transition).not.toHaveBeenCalled();
    expect(reorder.mock.calls[0][1]).toEqual(
      expect.objectContaining({ aboveId: "f", targetStatus: WorkItemStatus.WorkQueue }),
    );
    expect(reorder.mock.calls[0][1]?.belowId).toBeUndefined();
  });

  test("a card dropped into an empty column is only moved", async () => {
    const { transition, reorder } = spyRequests();
    const { onWorkItemUpdate } = renderColumn(WorkItemStatus.WorkQueue, []);

    const column = screen.getByText(WorkItemStatus.WorkQueue).closest("div")!;
    dropAt(column, "x", 50);

    await waitFor(() => expect(transition).toHaveBeenCalledWith("x", WorkItemStatus.WorkQueue));
    await waitFor(() => expect(onWorkItemUpdate).toHaveBeenCalled());
    await settle();
    expect(reorder).not.toHaveBeenCalled();
  });

  test("when a drop in a new column is refused the error is shown and the card stays where it was", async () => {
    const { transition, reorder } = spyRequests();
    reorder.mockRejectedValue(new Error("Refused by the server"));
    const { onError, onWorkItemUpdate } = renderColumn(WorkItemStatus.WorkQueue, ["e", "f"]);

    dropAt(card("f"), "x", CARD_HEIGHT + 30);

    await waitFor(() => expect(onError).toHaveBeenCalledTimes(1));
    expect(onError.mock.calls[0][0]).toMatch(/Refused by the server/);
    await settle();
    expect(reorder).toHaveBeenCalledTimes(1);
    expect(transition).not.toHaveBeenCalled();
    expect(onWorkItemUpdate).not.toHaveBeenCalled();
  });
});

describe("WorkItemCard", () => {
  test("shows human feedback badge when reason is set", () => {
    const item = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: "PR Awaiting Merge",
    });
    render(<WorkItemCard workItem={item} />);

    expect(screen.getByText("PR awaiting merge")).toBeTruthy();
  });

  test("hides badge when no reason is set", () => {
    const item = makeItem({
      status: WorkItemStatus.HumanFeedback,
      humanFeedbackReason: null,
    });
    render(<WorkItemCard workItem={item} />);

    expect(screen.queryByText("PR awaiting merge")).toBeFalsy();
    expect(screen.queryByText("Node failed")).toBeFalsy();
    expect(screen.queryByText("Rebase conflict")).toBeFalsy();
    expect(screen.queryByText("Human input needed")).toBeFalsy();
  });

  test("displays each reason in words, in its tone", () => {
    const reasons = [
      ["Node Failed", "Node failed", "pill-danger"],
      ["Rebase Conflict", "Rebase conflict", "pill-attention"],
      ["Human Input Needed", "Human input needed", "pill-attention"],
    ] as const;

    for (const [reason, label, tone] of reasons) {
      const item = makeItem({
        id: reason,
        status: WorkItemStatus.HumanFeedback,
        humanFeedbackReason: reason,
      });
      render(<WorkItemCard workItem={item} />);
      expect(screen.getByText(label).classList.contains(tone)).toBe(true);
    }
  });
});

describe("TaskboardColumn folding and Needs you", () => {
  const column = (status: WorkItemStatus, label: string, items: WorkItem[]) =>
    render(
      <TaskboardColumn
        status={status}
        label={label}
        workItems={items}
        total={items.length}
        onWorkItemUpdate={vi.fn()}
      />,
    );

  test("an empty column folds to a strip and opens, with a hint, when clicked", () => {
    column(WorkItemStatus.WorkQueue, "Work queue", []);

    const strip = screen.getByRole("button", { name: "Work queue: empty. Show column" });
    expect(strip.getAttribute("aria-expanded")).toBe("false");

    fireEvent.click(strip);
    expect(document.querySelector("p")?.textContent?.trim()).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Fold Work queue" }));
    expect(screen.getByRole("button", { name: "Work queue: empty. Show column" })).toBeTruthy();
  });

  test("a column with cards starts open, folds to a strip that counts them, and opens again", () => {
    column(WorkItemStatus.WorkQueue, "Work queue", [
      makeItem({ id: "a", title: "One", status: WorkItemStatus.Ready }),
      makeItem({ id: "b", title: "Two", status: WorkItemStatus.WorkQueue }),
    ]);
    expect(screen.getByText("One")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Fold Work queue" }));
    expect(screen.queryByText("One")).toBeNull();
    const strip = screen.getByRole("button", { name: "Work queue: 2 items. Show column" });
    expect(strip.textContent).toContain("2");

    fireEvent.click(strip);
    expect(screen.getByText("One")).toBeTruthy();
  });

  test("Done starts folded even with cards in it", () => {
    column(WorkItemStatus.Done, "Done", [
      makeItem({ title: "Shipped", status: WorkItemStatus.Done }),
    ]);
    expect(screen.queryByText("Shipped")).toBeNull();
    expect(screen.getByRole("button", { name: "Done: 1 item. Show column" })).toBeTruthy();
  });

  test("Running starts open even when empty, so what starts is seen at once", () => {
    column(WorkItemStatus.Running, "Running", []);
    expect(screen.queryByRole("button", { name: /Running: empty. Show column/ })).toBeNull();
    expect(screen.getByText("Ready items start here while the scheduler is running.")).toBeTruthy();
  });

  test("a choice handed in wins over the column's own rule, and a fold is handed back out", () => {
    const onFoldChange = vi.fn();
    render(
      <TaskboardColumn
        status={WorkItemStatus.Done}
        label="Done"
        workItems={[makeItem({ title: "Shipped", status: WorkItemStatus.Done })]}
        total={1}
        onWorkItemUpdate={vi.fn()}
        foldChoice={false}
        onFoldChange={onFoldChange}
      />,
    );
    expect(screen.getByText("Shipped")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Fold Done" }));
    expect(onFoldChange).toHaveBeenCalledWith(true);
  });

  test("a folded Needs you with something waiting is still marked", () => {
    const { container } = column(WorkItemStatus.HumanFeedback, "Needs you", [
      makeItem({ status: WorkItemStatus.HumanFeedback }),
    ]);
    fireEvent.click(screen.getByRole("button", { name: "Fold Needs you" }));
    expect(
      container.querySelector(".taskboard-column--collapsed.taskboard-column--needs"),
    ).not.toBeNull();
  });

  test("Needs you is marked while something waits in it", () => {
    const { container } = column(WorkItemStatus.HumanFeedback, "Needs you", [
      makeItem({ status: WorkItemStatus.HumanFeedback }),
    ]);
    expect(container.querySelector(".taskboard-column--needs")).not.toBeNull();
  });
});

const GAP = 8;
const BODY_PADDING = 12;
const SLOT = CARD_HEIGHT + GAP;
const cardTop = (i: number) => BODY_PADDING + i * SLOT;

/**
 * Lays each column's cards out one under the other, CARD_HEIGHT tall with GAP
 * between them and BODY_PADDING above the first (jsdom has no layout). A card
 * and anything inside it measure as the card; anything else, such as the
 * column, its header or body, spans 200 wide and room below the last card.
 */
function layOutWithGaps(...columns: string[][]) {
  vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
    const id = this.closest("[data-work-item-id]")?.getAttribute("data-work-item-id");
    const ids = columns.find((c) => id !== undefined && c.includes(id!));
    const top = ids ? cardTop(ids.indexOf(id!)) : 0;
    const height = ids ? CARD_HEIGHT : cardTop(Math.max(...columns.map((c) => c.length)) + 2);
    return {
      x: 0,
      y: top,
      top,
      left: 0,
      right: 200,
      bottom: top + height,
      width: 200,
      height,
      toJSON: () => ({}),
    } as DOMRect;
  });
}

type DragEventName = "dragEnter" | "dragOver" | "dragLeave" | "drop" | "dragEnd";

/**
 * Fires a drag event as a browser would (jsdom has no DragEvent, so pointer
 * position and relatedTarget are set on the event), and reports whether it
 * was cancelled and the dropEffect it was given.
 */
function drag(
  name: DragEventName,
  target: Element,
  {
    clientY = 0,
    clientX = 100,
    relatedTarget = null,
    draggedId = "",
  }: {
    clientY?: number;
    clientX?: number;
    relatedTarget?: Element | null;
    draggedId?: string;
  } = {},
) {
  const dataTransfer = {
    getData: () => draggedId,
    setData: () => {},
    dropEffect: "none",
    effectAllowed: "move",
  };
  const event = createEvent[name](target, { dataTransfer });
  Object.defineProperty(event, "clientX", { value: clientX });
  Object.defineProperty(event, "clientY", { value: clientY });
  Object.defineProperty(event, "pageY", { value: clientY });
  Object.defineProperty(event, "relatedTarget", { value: relatedTarget });
  const prevented = !fireEvent(target, event);
  return { prevented, dropEffect: dataTransfer.dropEffect };
}

function columnOfStatus(status: WorkItemStatus): HTMLElement {
  return document.querySelector(`.taskboard-column[data-status="${status}"]`) as HTMLElement;
}

/**
 * The slot the column's drop line shows: i when it is above card i, the card
 * count when it is below the last card, null when there is none. Fails on more
 * than one line, or a line below a card other than the last.
 */
function lineIn(column: Element): number | null {
  const cards = Array.from(column.querySelectorAll("[data-work-item-id]"));
  const lines: number[] = [];
  cards.forEach((c, i) => {
    if (c.classList.contains("work-item-card--drop-above")) lines.push(i);
    if (c.classList.contains("work-item-card--drop-below"))
      lines.push(i === cards.length - 1 ? cards.length : -1);
  });
  expect(lines.length).toBeLessThanOrEqual(1);
  expect(lines).not.toContain(-1);
  return lines.length === 0 ? null : lines[0];
}

const titleOf = (id: string) => card(id).querySelector(".work-item-title")!;
const bodyOf = (column: Element) => column.querySelector(".taskboard-column-body")!;

describe("TaskboardColumn drop line", () => {
  function renderOpen(status: WorkItemStatus, ids: string[]) {
    render(
      <TaskboardColumn
        status={status}
        label={status}
        workItems={itemsIn(status, ids)}
        total={ids.length}
        onWorkItemUpdate={vi.fn()}
        onError={vi.fn()}
        foldChoice={false}
      />,
    );
    return columnOfStatus(status);
  }

  /** Backlog [a, b, c] and Work queue [e, f], side by side as on the board. */
  function renderTwo() {
    render(
      <>
        <TaskboardColumn
          status={WorkItemStatus.Backlog}
          label="Backlog"
          workItems={itemsIn(WorkItemStatus.Backlog, ["a", "b", "c"])}
          total={3}
          onWorkItemUpdate={vi.fn()}
          onError={vi.fn()}
          foldChoice={false}
        />
        <TaskboardColumn
          status={WorkItemStatus.WorkQueue}
          label="Work queue"
          workItems={itemsIn(WorkItemStatus.WorkQueue, ["e", "f"])}
          total={2}
          onWorkItemUpdate={vi.fn()}
          onError={vi.fn()}
          foldChoice={false}
        />
      </>,
    );
    layOutWithGaps(["a", "b", "c"], ["e", "f"]);
    return {
      backlog: columnOfStatus(WorkItemStatus.Backlog),
      queue: columnOfStatus(WorkItemStatus.WorkQueue),
    };
  }

  test.each([
    ["the body padding above the first card", 4, 0],
    ["the upper half of the first card", cardTop(0) + 10, 0],
    ["the gap between the first and second card", cardTop(0) + CARD_HEIGHT + GAP / 2, 1],
    ["the gap between the second and third card", cardTop(1) + CARD_HEIGHT + GAP / 2, 2],
    ["the space below the last card", cardTop(3) + 40, 3],
  ])("a card dragged over %s (y %i) shows the line at slot %i", (_where, clientY, slot) => {
    const column = renderOpen(WorkItemStatus.Backlog, ["a", "b", "c"]);
    layOutWithGaps(["a", "b", "c"]);

    drag("dragOver", bodyOf(column), { clientY });

    expect(lineIn(column)).toBe(slot);
  });

  test("a card dragged over the header above the cards shows the line above the first card", () => {
    const column = renderOpen(WorkItemStatus.Backlog, ["a", "b", "c"]);
    layOutWithGaps(["a", "b", "c"]);

    drag("dragOver", column.querySelector(".taskboard-column-title")!, { clientY: 2 });

    expect(lineIn(column)).toBe(0);
  });

  test("dragging slowly from the top to the bottom of a column moves the line one slot at a time", () => {
    const ids = ["a", "b", "c", "d"];
    const column = renderOpen(WorkItemStatus.Backlog, ids);
    layOutWithGaps(ids);

    const shown: (number | null)[] = [];
    for (let y = 0; y < cardTop(ids.length) + 40; y += 3) {
      const over = ids.findIndex((_, i) => y >= cardTop(i) && y < cardTop(i) + CARD_HEIGHT);
      const target = over >= 0 ? titleOf(ids[over]) : bodyOf(column);
      drag("dragOver", target, { clientY: y });
      const middleBelow = ids.findIndex((_, i) => y < cardTop(i) + CARD_HEIGHT / 2);
      const expected = middleBelow >= 0 ? middleBelow : ids.length;
      expect(lineIn(column), `pointer at ${y}`).toBe(expected);
      if (shown[shown.length - 1] !== expected) shown.push(expected);
    }

    expect(shown).toEqual([0, 1, 2, 3, 4]);
  });

  test("the line and highlight stay while the pointer moves between a card and its child elements", () => {
    const column = renderOpen(WorkItemStatus.Backlog, ["a", "b", "c"]);
    layOutWithGaps(["a", "b", "c"]);
    const y = cardTop(1) + 20;

    drag("dragEnter", card("b"), { clientY: y });
    drag("dragOver", card("b"), { clientY: y });
    expect(lineIn(column)).toBe(1);

    drag("dragEnter", titleOf("b"), { clientY: y });
    drag("dragLeave", card("b"), { clientY: y, relatedTarget: titleOf("b") });
    expect(lineIn(column)).toBe(1);
    expect(column.classList.contains("drag-over")).toBe(true);

    drag("dragEnter", card("b"), { clientY: y });
    drag("dragLeave", titleOf("b"), { clientY: y, relatedTarget: card("b") });
    expect(lineIn(column)).toBe(1);

    drag("dragLeave", card("b"), { clientY: y, relatedTarget: bodyOf(column) });
    expect(lineIn(column)).toBe(1);

    // Firefox may leave relatedTarget out; the pointer is still inside the column.
    drag("dragLeave", card("b"), { clientY: y, clientX: 100, relatedTarget: null });
    expect(lineIn(column)).toBe(1);
    expect(column.classList.contains("drag-over")).toBe(true);
  });

  test.each([
    [
      "the pointer leaves the column",
      ({ backlog }: { backlog: HTMLElement; queue: HTMLElement }) =>
        drag("dragLeave", backlog, { clientY: 50, relatedTarget: document.body }),
    ],
    [
      "the pointer leaves the column with no related target",
      ({ backlog }: { backlog: HTMLElement; queue: HTMLElement }) =>
        drag("dragLeave", backlog, { clientY: 50, clientX: 500, relatedTarget: null }),
    ],
    [
      "the card is dropped in the column",
      () => drag("drop", card("b"), { clientY: cardTop(1) + 20 }),
    ],
    [
      "the card is dropped in another column",
      ({ queue }: { backlog: HTMLElement; queue: HTMLElement }) =>
        drag("drop", bodyOf(queue), { clientY: 50 }),
    ],
    [
      "the drag ends or is cancelled on a card in another column",
      () => drag("dragEnd", card("e"), { clientY: 50 }),
    ],
  ])("the line and highlight clear when %s", (_when, act) => {
    const columns = renderTwo();
    drag("dragEnter", card("b"), { clientY: cardTop(1) + 20 });
    drag("dragOver", card("b"), { clientY: cardTop(1) + 20 });
    expect(lineIn(columns.backlog)).toBe(1);
    expect(columns.backlog.classList.contains("drag-over")).toBe(true);

    act(columns);

    expect(lineIn(columns.backlog)).toBeNull();
    expect(columnOfStatus(WorkItemStatus.Backlog).classList.contains("drag-over")).toBe(false);
  });

  test("a drag cancelled over the column with no dragend clears the line once no dragover follows", () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const advance = (ms: number) =>
      act(() => {
        vi.advanceTimersByTime(ms);
      });
    try {
      const column = renderOpen(WorkItemStatus.Backlog, ["a", "b", "c"]);
      layOutWithGaps(["a", "b", "c"]);
      const y = cardTop(1) + 20;
      drag("dragEnter", card("b"), { clientY: y });
      drag("dragOver", card("b"), { clientY: y });

      drag("dragLeave", card("b"), { clientY: y, relatedTarget: null });
      advance(300);
      drag("dragOver", card("b"), { clientY: y });
      advance(1000);
      expect(lineIn(column)).toBe(1);

      // Chrome's dragleave on Escape: no relatedTarget, the pointer still over the column.
      drag("dragLeave", card("b"), { clientY: y, relatedTarget: null });
      advance(1000);
      expect(lineIn(column)).toBeNull();
      expect(column.classList.contains("drag-over")).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  test("a folded column opened by a drag stays open across child leaves and folds back on leaving", () => {
    render(
      <TaskboardColumn
        status={WorkItemStatus.Done}
        label="Done"
        workItems={itemsIn(WorkItemStatus.Done, ["a", "b"])}
        total={2}
        onWorkItemUpdate={vi.fn()}
        onError={vi.fn()}
      />,
    );
    layOutWithGaps(["a", "b"]);
    const strip = screen.getByRole("button", { name: "Done: 2 items. Show column" });

    drag("dragEnter", strip, { clientY: 30 });
    drag("dragOver", strip, { clientY: 30 });
    expect(screen.queryByRole("button", { name: /Show column/ })).toBeNull();

    drag("dragEnter", card("a"), { clientY: 30 });
    drag("dragOver", card("a"), { clientY: 30 });
    drag("dragEnter", titleOf("a"), { clientY: 30 });
    drag("dragLeave", card("a"), { clientY: 30, relatedTarget: titleOf("a") });
    drag("dragEnter", card("a"), { clientY: 30 });
    drag("dragLeave", titleOf("a"), { clientY: 30, relatedTarget: card("a") });
    expect(screen.queryByRole("button", { name: /Show column/ })).toBeNull();
    expect(lineIn(columnOfStatus(WorkItemStatus.Done))).toBe(0);

    drag("dragLeave", columnOfStatus(WorkItemStatus.Done), {
      clientY: 30,
      relatedTarget: document.body,
    });
    expect(screen.getByRole("button", { name: "Done: 2 items. Show column" })).toBeTruthy();
  });

  const targets: [string, () => Element][] = [
    [
      "a card",
      () => {
        renderOpen(WorkItemStatus.Backlog, ["a", "b"]);
        return card("a");
      },
    ],
    ["the gap between cards", () => bodyOf(renderOpen(WorkItemStatus.Backlog, ["a", "b"]))],
    [
      "the header",
      () =>
        renderOpen(WorkItemStatus.Backlog, ["a", "b"]).querySelector(".taskboard-column-header")!,
    ],
    [
      "a folded column's strip",
      () => {
        render(
          <TaskboardColumn
            status={WorkItemStatus.Done}
            label="Done"
            workItems={itemsIn(WorkItemStatus.Done, ["a"])}
            total={1}
            onWorkItemUpdate={vi.fn()}
          />,
        );
        return screen.getByRole("button", { name: "Done: 1 item. Show column" });
      },
    ],
  ];

  test.each(
    targets.flatMap(([where, setUp]) =>
      (["dragEnter", "dragOver"] as const).map((name) => [name, where, setUp] as const),
    ),
  )("%s over %s is accepted as a move", (name, _where, setUp) => {
    const target = setUp();
    layOutWithGaps(["a", "b"]);

    const { prevented, dropEffect } = drag(name, target, { clientY: cardTop(0) + CARD_HEIGHT + 4 });

    expect(prevented).toBe(true);
    expect(dropEffect).toBe("move");
  });

  test("while a card is over a column, the rest of the page still refuses drops", () => {
    const column = renderOpen(WorkItemStatus.Backlog, ["a", "b"]);
    const outside = document.createElement("div");
    document.body.appendChild(outside);
    layOutWithGaps(["a", "b"]);
    drag("dragEnter", card("a"), { clientY: 20 });
    drag("dragOver", card("a"), { clientY: 20 });
    expect(lineIn(column)).toBe(0);

    expect(drag("dragEnter", outside, { clientX: 500 }).prevented).toBe(false);
    expect(drag("dragOver", outside, { clientX: 500 }).prevented).toBe(false);
    outside.remove();
  });

  test("dragging on within the same slot does not re-render the column", () => {
    const onRender = vi.fn();
    render(
      <Profiler id="column" onRender={onRender}>
        <TaskboardColumn
          status={WorkItemStatus.Backlog}
          label="Backlog"
          workItems={itemsIn(WorkItemStatus.Backlog, ["a", "b", "c"])}
          total={3}
          onWorkItemUpdate={vi.fn()}
          foldChoice={false}
        />
      </Profiler>,
    );
    layOutWithGaps(["a", "b", "c"]);
    drag("dragOver", card("b"), { clientY: cardTop(1) + 10 });
    drag("dragOver", titleOf("b"), { clientY: cardTop(1) + 20 });
    const renders = onRender.mock.calls.length;

    for (let y = cardTop(1) + 22; y < cardTop(1) + CARD_HEIGHT / 2; y += 2)
      drag("dragOver", titleOf("b"), { clientY: y });

    expect(onRender.mock.calls.length).toBe(renders);
  });

  test.each([
    ["within the column", ["a", "b", "c", "d"], "a", { aboveId: "c", belowId: "d" }],
    [
      "from another column",
      ["a", "b", "c", "d"],
      "x",
      { aboveId: "c", belowId: "d", targetStatus: WorkItemStatus.Backlog },
    ],
  ])(
    "a card dropped %s in a gap lands at the slot the line showed",
    async (_how, ids, draggedId, request) => {
      const reorder = vi
        .spyOn(authServices.workItemService, "reorder")
        .mockImplementation(async (id: string) =>
          makeItem({ id, title: `Item ${id}`, status: WorkItemStatus.Backlog }),
        );
      const column = renderOpen(WorkItemStatus.Backlog, ids);
      layOutWithGaps(ids);
      const y = cardTop(2) + CARD_HEIGHT + GAP / 2;

      drag("dragOver", bodyOf(column), { clientY: y, draggedId });
      expect(lineIn(column)).toBe(3);
      drag("drop", bodyOf(column), { clientY: y, draggedId });

      await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
      expect(reorder.mock.calls[0][0]).toBe(draggedId);
      expect(reorder.mock.calls[0][1]).toEqual(request);
    },
  );
});
