import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, createEvent, fireEvent, waitFor } from "@testing-library/react";
import { useState, type ComponentProps } from "react";
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
    renderColumn(WorkItemStatus.Ready, ["a", "b", "c"]);

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

  test("a card from another column is moved first, then placed exactly where it was dropped", async () => {
    const { transition, reorder } = spyRequests();
    const { onWorkItemUpdate, onError } = renderColumn(WorkItemStatus.Ready, ["e", "f", "g"]);

    dropAt(card("f"), "x", CARD_HEIGHT + 30);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(transition).toHaveBeenCalledWith("x", WorkItemStatus.Ready);
    expect(transition.mock.invocationCallOrder[0]).toBeLessThan(
      reorder.mock.invocationCallOrder[0],
    );
    expect(reorder.mock.calls[0][0]).toBe("x");
    expect(reorder.mock.calls[0][1]).toEqual(
      expect.objectContaining({ aboveId: "e", belowId: "f" }),
    );
    await waitFor(() =>
      expect(onWorkItemUpdate).toHaveBeenLastCalledWith(
        expect.objectContaining({ id: "x", priority: -42 }),
      ),
    );
    expect(onError).not.toHaveBeenCalled();
  });

  test("a card from another column dropped on the lower half of the last card lands at the bottom", async () => {
    const { transition, reorder } = spyRequests();
    renderColumn(WorkItemStatus.Ready, ["e", "f"]);

    dropAt(card("f"), "x", CARD_HEIGHT + 70);

    await waitFor(() => expect(reorder).toHaveBeenCalledTimes(1));
    expect(transition).toHaveBeenCalledWith("x", WorkItemStatus.Ready);
    expect(reorder.mock.calls[0][1]).toEqual(expect.objectContaining({ aboveId: "f" }));
    expect(reorder.mock.calls[0][1]?.belowId).toBeUndefined();
  });

  test("a card dropped into an empty column is only moved", async () => {
    const { transition, reorder } = spyRequests();
    const { onWorkItemUpdate } = renderColumn(WorkItemStatus.Ready, []);

    const column = screen.getByText(WorkItemStatus.Ready).closest("div")!;
    dropAt(column, "x", 50);

    await waitFor(() => expect(transition).toHaveBeenCalledWith("x", WorkItemStatus.Ready));
    await waitFor(() => expect(onWorkItemUpdate).toHaveBeenCalled());
    await settle();
    expect(reorder).not.toHaveBeenCalled();
  });

  test("when the move to the new column fails the error is shown and nothing is reordered", async () => {
    const { transition, reorder } = spyRequests();
    transition.mockRejectedValue(new Error("Refused by the server"));
    const { onError } = renderColumn(WorkItemStatus.Ready, ["e", "f"]);

    dropAt(card("f"), "x", CARD_HEIGHT + 30);

    await waitFor(() => expect(onError).toHaveBeenCalledTimes(1));
    expect(onError.mock.calls[0][0]).toMatch(/Refused by the server/);
    await settle();
    expect(reorder).not.toHaveBeenCalled();
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
    expect(screen.getByText(/Move a backlog item here/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Fold Work queue" }));
    expect(screen.getByRole("button", { name: "Work queue: empty. Show column" })).toBeTruthy();
  });

  test("a column with cards starts open, folds to a strip that counts them, and opens again", () => {
    column(WorkItemStatus.Ready, "Ready", [
      makeItem({ id: "a", title: "One", status: WorkItemStatus.Ready }),
      makeItem({ id: "b", title: "Two", status: WorkItemStatus.Ready }),
    ]);
    expect(screen.getByText("One")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Fold Ready" }));
    expect(screen.queryByText("One")).toBeNull();
    const strip = screen.getByRole("button", { name: "Ready: 2 items. Show column" });
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
