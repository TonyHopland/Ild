import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, fireEvent, cleanup, act, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import WorkItemModalV2 from "./WorkItemModalV2";
import { NodeType, WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import type { LoopRun } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import {
  FIXED_NODE_OUTPUTS,
  PINNED_VERSION,
  RESERVED_PR_OUTPUTS,
  graphNode,
  parkedRun,
  stageParkedNode,
  type ParkedNode,
} from "../../test-support.parkedNode";

// The feedback pane offers an output as a button only when the node the run is
// parked on shows that output to the user. It learns that from the run's pinned
// template version, and offers nothing while it does not know.

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  localStorage.clear();
});

const MB = 1024 * 1024;
const PR_REASON = "PR Awaiting Merge";
const PR_ACTIONS = "OnSuccess,OnFailure,on_merged,on_ci_failed";

function makeParkedWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test Work Item",
    description: "",
    status: WorkItemStatus.HumanFeedback,
    priority: WorkItemPriority.Medium,
    tags: [],
    conversation: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-1",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: "Human Input Needed",
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: "run-1",
    dependencyIds: [],
    dependentIds: [],
    ...overrides,
  };
}

type Handler = (m: { type: string; payload: unknown; timestamp: string }) => void;

/** Everything the dialog reads besides the parked node, plus a way to raise hub events. */
function mockDialogServices() {
  const handlers = new Map<string, Set<Handler>>();
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn((event: string, handler: Handler) => {
      const set = handlers.get(event) ?? new Set<Handler>();
      set.add(handler);
      handlers.set(event, set);
    }),
    off: vi.fn((event: string, handler: Handler) => handlers.get(event)?.delete(handler)),
    invoke: vi.fn(() => Promise.resolve(null)),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "getById").mockResolvedValue(makeParkedWorkItem());
  vi.spyOn(authServices.workItemService, "listEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.workItemService, "listRequestedEditProposals").mockResolvedValue([]);
  vi.spyOn(authServices.loopRunService, "getEvents").mockResolvedValue({
    entries: [],
    nextCursor: 0,
    hasMore: false,
  });
  vi.spyOn(authServices.settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * MB,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * MB,
  });
  return {
    emit: (event: string, payload: unknown) =>
      handlers.get(event)?.forEach((handler) => handler({ type: event, payload, timestamp: "" })),
  };
}

function dialogFor(workItem: WorkItem) {
  return (
    <MemoryRouter>
      <WorkItemModalV2 workItem={workItem} onClose={vi.fn()} onSave={vi.fn()} />
    </MemoryRouter>
  );
}

/** Lets every answered read, and whatever it sets off, land. */
async function settled() {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

async function renderDialog(workItem: WorkItem) {
  const result = render(dialogFor(workItem));
  await settled();
  return result;
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

const pane = () => document.querySelector(".wiv2-feedback") as HTMLElement;
const buttons = () =>
  within(pane())
    .queryAllByRole("button")
    .map((button) => button.textContent ?? "");

function expectButtons(expected: string[]) {
  expect(buttons()).toHaveLength(expected.length);
  expect(new Set(buttons())).toEqual(new Set(expected));
}

const LOAD_ERROR = /outputs (could not|couldn't) be loaded/i;

const human = (outputs: unknown[]): ParkedNode => ({
  type: NodeType.Human,
  config: { outputs },
});
const pr = (outputs?: unknown[]): ParkedNode => ({
  type: NodeType.PR,
  config: outputs ? { outputs } : {},
});

/** A PR node's outputs as the server stores them when nothing sets `visible`. */
const STORED_PR_OUTPUTS = [
  { name: "OnSuccess" },
  { name: "OnFailure" },
  ...RESERVED_PR_OUTPUTS.map((name) => ({ name, reserved: true })),
];

describe("the feedback pane offers only the outputs the parked node shows", () => {
  const CASES: Array<{
    name: string;
    node: ParkedNode;
    item: Partial<WorkItem>;
    buttons: string[];
  }> = [
    {
      name: "a hidden success output and a hidden named output have no button",
      node: human([
        { name: "OnSuccess", visible: false },
        { name: "OnFailure" },
        { name: "Needs work" },
        { name: "Escalate", visible: false },
      ]),
      item: { humanFeedbackActions: "OnSuccess,Needs work,Escalate,OnFailure" },
      buttons: ["Needs work", "Reject"],
    },
    {
      name: "a hidden failure output has no Reject",
      node: human([{ name: "OnSuccess" }, { name: "OnFailure", visible: false }]),
      item: { humanFeedbackActions: "OnSuccess,OnFailure" },
      buttons: ["Approve"],
    },
    {
      name: "nothing hidden leaves every output as before",
      node: human([{ name: "OnSuccess" }, { name: "OnFailure" }, { name: "Needs work" }]),
      item: { humanFeedbackActions: "OnSuccess,Needs work,OnFailure" },
      buttons: ["Approve", "Needs work", "Reject"],
    },
    {
      name: "a PR node whose config has no outputs hides its wired reserved outputs",
      node: pr(),
      item: { humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS },
      buttons: ["Approve", "Merge", "Reject"],
    },
    {
      name: "a PR node stored with no visible anywhere hides its wired reserved outputs",
      node: pr(STORED_PR_OUTPUTS),
      item: { humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS },
      buttons: ["Approve", "Merge", "Reject"],
    },
    {
      name: "a reserved PR output set to visible gets its button",
      node: pr(
        STORED_PR_OUTPUTS.map((output) =>
          output.name === "on_merged" ? { ...output, visible: true } : output,
        ),
      ),
      item: { humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS },
      buttons: ["Approve", "Merge", "on_merged", "Reject"],
    },
    {
      name: "an output named like a reserved one is offered on a Human node",
      node: human([{ name: "OnSuccess" }, { name: "on_merged" }]),
      item: { humanFeedbackActions: "OnSuccess,on_merged" },
      buttons: ["Approve", "on_merged"],
    },
    {
      name: "with no actions listed, a hidden success output leaves only Reject",
      node: human([{ name: "OnSuccess", visible: false }, { name: "OnFailure" }]),
      item: { humanFeedbackActions: null },
      buttons: ["Reject"],
    },
    {
      name: "with no actions listed and nothing hidden, Approve and Reject are offered",
      node: human([]),
      item: { humanFeedbackActions: null },
      buttons: ["Approve", "Reject"],
    },
    {
      name: "with every listed output hidden there is no button, and no fallback to Approve and Reject",
      node: human([
        { name: "OnSuccess", visible: false },
        { name: "OnFailure", visible: false },
        { name: "later", visible: false },
      ]),
      item: { humanFeedbackActions: "OnSuccess,OnFailure,later" },
      buttons: [],
    },
    {
      name: "with no actions listed and success and failure hidden there is no button",
      node: human([
        { name: "OnSuccess", visible: false },
        { name: "OnFailure", visible: false },
      ]),
      item: { humanFeedbackActions: null },
      buttons: [],
    },
    {
      name: "a PR node with every output hidden still has Merge",
      node: pr(STORED_PR_OUTPUTS.map((output) => ({ ...output, visible: false }))),
      item: { humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS },
      buttons: ["Merge"],
    },
  ];

  test.each(CASES)("$name", async ({ node, item, buttons: expected }) => {
    mockDialogServices();
    const staged = stageParkedNode(node);

    await renderDialog(makeParkedWorkItem(item));

    expect(staged.getVersionGraph).toHaveBeenCalledWith(
      PINNED_VERSION.loopTemplateId,
      PINNED_VERSION.templateVersion,
    );
    expectButtons(expected);
    expect(pane().textContent).not.toMatch(LOAD_ERROR);
  });

  test("a visible named output beside hidden ones submits as before", async () => {
    mockDialogServices();
    stageParkedNode(
      human([{ name: "OnSuccess", visible: false }, { name: "OnFailure" }, { name: "Needs work" }]),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackEdge")
      .mockResolvedValue(undefined);
    await renderDialog(
      makeParkedWorkItem({ humanFeedbackActions: "OnSuccess,Needs work,OnFailure" }),
    );
    expectButtons(["Needs work", "Reject"]);

    await act(async () => {
      fireEvent.change(pane().querySelector("textarea") as HTMLTextAreaElement, {
        target: { value: "Have a look" },
      });
    });
    await act(async () => {
      fireEvent.click(within(pane()).getByRole("button", { name: "Needs work" }));
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer).toHaveBeenCalledWith("wi-1", "Needs work", "Have a look");
  });

  test("an output the parked node marks to confirm asks before it is sent", async () => {
    mockDialogServices();
    stageParkedNode(
      human([{ name: "OnSuccess" }, { name: "OnFailure" }, { name: "Clean up", confirm: true }]),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackEdge")
      .mockResolvedValue(undefined);
    await renderDialog(
      makeParkedWorkItem({ humanFeedbackActions: "OnSuccess,Clean up,OnFailure" }),
    );

    fireEvent.click(within(pane()).getByRole("button", { name: "Clean up" }));
    const confirm = within(pane()).getByRole("dialog", { name: "Confirm Clean up" });
    expect(answer).not.toHaveBeenCalled();
    await act(async () => {
      fireEvent.click(within(confirm).getByRole("button", { name: "Clean up" }));
    });

    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer).toHaveBeenCalledWith("wi-1", "Clean up", "");
  });

  test("a valid colour stored on the parked node's output colours its button, and nothing else does", async () => {
    mockDialogServices();
    stageParkedNode(
      human([
        { name: "OnSuccess", color: "#7E22CE" },
        { name: "OnFailure", color: "red" },
        { name: "Needs work", color: "#fde047" },
        { name: "Later" },
      ]),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackEdge")
      .mockResolvedValue(undefined);
    await renderDialog(
      makeParkedWorkItem({ humanFeedbackActions: "OnSuccess,Needs work,Later,OnFailure" }),
    );

    const cssColor = (color: string) => {
      const probe = document.createElement("span");
      probe.style.color = color;
      return probe.style.color;
    };
    const button = (name: string) => within(pane()).getByRole("button", { name });
    expect(button("Approve").style.backgroundColor).toBe(cssColor("#7e22ce"));
    expect(button("Needs work").style.backgroundColor).toBe(cssColor("#fde047"));
    expect(button("Reject").getAttribute("style")).toBeNull();
    expect(button("Later").getAttribute("style")).toBeNull();

    await act(async () => {
      fireEvent.click(button("Needs work"));
    });
    await waitFor(() => expect(answer).toHaveBeenCalledTimes(1));
    expect(answer).toHaveBeenCalledWith("wi-1", "Needs work", "");
  });

  test("a confirmation open when the work item changes is dropped, and nothing is sent", async () => {
    mockDialogServices();
    stageParkedNode(
      human([{ name: "OnSuccess" }, { name: "OnFailure" }, { name: "Clean up", confirm: true }]),
    );
    const answer = vi
      .spyOn(authServices.workItemService, "humanFeedbackEdge")
      .mockResolvedValue(undefined);
    const item = makeParkedWorkItem({ humanFeedbackActions: "OnSuccess,Clean up,OnFailure" });
    const { rerender } = await renderDialog(item);

    fireEvent.click(within(pane()).getByRole("button", { name: "Clean up" }));
    expect(within(pane()).getByRole("dialog", { name: "Confirm Clean up" })).toBeTruthy();

    rerender(dialogFor({ ...item }));
    await settled();

    expect(within(pane()).queryByRole("dialog")).toBeNull();
    expectButtons(["Approve", "Clean up", "Reject"]);
    expect(answer).not.toHaveBeenCalled();
  });

  test("Merge asks for confirmation and merges, whatever the outputs show", async () => {
    mockDialogServices();
    stageParkedNode(pr(STORED_PR_OUTPUTS.map((output) => ({ ...output, visible: false }))));
    const merge = vi.spyOn(authServices.workItemService, "mergePr").mockResolvedValue({
      branchDeleted: true,
      warning: null,
    });
    await renderDialog(
      makeParkedWorkItem({ humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS }),
    );

    fireEvent.click(within(pane()).getByRole("button", { name: "Merge" }));
    const confirm = within(pane()).getByRole("dialog", { name: "Confirm merge" });
    await act(async () => {
      fireEvent.click(within(confirm).getByRole("button", { name: "Confirm Merge" }));
    });

    await waitFor(() => expect(merge).toHaveBeenCalledWith("wi-1", true));
  });
});

describe("the feedback pane never guesses the parked node's outputs", () => {
  test("no output button is offered until the node's outputs are known", async () => {
    mockDialogServices();
    const staged = stageParkedNode(human([{ name: "OnSuccess" }, { name: "OnFailure" }]));
    const read = deferred<LoopRun>();
    staged.getRun.mockReturnValue(read.promise);

    await renderDialog(makeParkedWorkItem());

    expectButtons([]);
    expect(pane().textContent).not.toMatch(LOAD_ERROR);

    read.resolve(parkedRun("n-parked"));
    await settled();

    expectButtons(["Approve", "Reject"]);
  });

  test("Merge is there while a PR node's outputs are still being read", async () => {
    mockDialogServices();
    const staged = stageParkedNode(pr());
    staged.getRun.mockReturnValue(new Promise<LoopRun>(() => {}));

    await renderDialog(
      makeParkedWorkItem({ humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS }),
    );

    expectButtons(["Merge"]);
  });

  const UNKNOWN_CASES: Array<{
    name: string;
    item?: Partial<WorkItem>;
    break: (staged: ReturnType<typeof stageParkedNode>) => void;
  }> = [
    {
      name: "the run cannot be read",
      break: (staged) => staged.getRun.mockRejectedValue({ status: 404, message: "no run" }),
    },
    {
      name: "the pinned version graph cannot be read",
      break: (staged) =>
        staged.getVersionGraph.mockRejectedValue({ status: 404, message: "Version 1 not found" }),
    },
    {
      name: "the fixed outputs cannot be read",
      break: (staged) =>
        staged.getNodeOutputs.mockRejectedValue({ status: 500, message: "unavailable" }),
    },
    {
      name: "the run's current node is not in the graph",
      break: (staged) => staged.getRun.mockResolvedValue(parkedRun("n-gone")),
    },
    {
      name: "the run names no current node",
      break: (staged) => staged.getRun.mockResolvedValue(parkedRun(null)),
    },
    {
      name: "the item has no current run",
      item: { currentLoopRunId: null },
      break: () => {},
    },
  ];

  test.each(UNKNOWN_CASES)(
    "when $name, no output button is offered and the pane says so",
    async ({ item, break: breakRead }) => {
      mockDialogServices();
      breakRead(stageParkedNode(human([{ name: "OnSuccess" }, { name: "OnFailure" }])));

      await renderDialog(
        makeParkedWorkItem({ humanFeedbackActions: "OnSuccess,Needs work,OnFailure", ...item }),
      );

      expectButtons([]);
      expect(pane().textContent).toMatch(LOAD_ERROR);
    },
  );

  test("Merge is still there when a PR node's outputs cannot be read", async () => {
    mockDialogServices();
    stageParkedNode(pr()).getVersionGraph.mockRejectedValue({ status: 500, message: "down" });

    await renderDialog(
      makeParkedWorkItem({ humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS }),
    );

    expectButtons(["Merge"]);
    expect(pane().textContent).toMatch(LOAD_ERROR);
  });
});

describe("a work item's actions are filtered only with the node read for that same state", () => {
  const NODE_A = graphNode({
    id: "n-a",
    type: NodeType.Human,
    config: { outputs: [{ name: "OnSuccess" }, { name: "OnFailure" }] },
  });
  const NODE_B = graphNode({ id: "n-b", type: NodeType.PR, config: {} });
  const RESERVED_BUTTONS = ["on_merged", "on_ci_failed"];

  const onHumanNode = () => makeParkedWorkItem({ humanFeedbackActions: "OnSuccess,OnFailure" });
  const onPrNode = () =>
    makeParkedWorkItem({ humanFeedbackReason: PR_REASON, humanFeedbackActions: PR_ACTIONS });

  /** Both nodes are in the pinned graph; each run read is answered by hand. */
  function stageTwoNodes() {
    const reads: Array<ReturnType<typeof deferred<LoopRun>>> = [];
    const getRun = vi.spyOn(authServices.loopRunService, "getById").mockImplementation(() => {
      const read = deferred<LoopRun>();
      reads.push(read);
      return read.promise;
    });
    const getVersionGraph = vi
      .spyOn(authServices.loopTemplateService, "getVersionGraph")
      .mockResolvedValue({ nodes: [NODE_A, NODE_B], edges: [] });
    const getNodeOutputs = vi
      .spyOn(authServices.loopTemplateService, "getNodeOutputs")
      .mockResolvedValue(FIXED_NODE_OUTPUTS);
    /** Answers every run read asked for so far and not answered yet. */
    const answer = async (from: number, nodeId: string) => {
      for (const read of reads.slice(from)) read.resolve(parkedRun(nodeId));
      await settled();
    };
    return { reads, answer, getRun, getVersionGraph, getNodeOutputs };
  }

  /** Every button the pane has ever shown, by name, including ones since removed. */
  function watchButtons() {
    const seen = new Set<string>();
    const record = () => buttons().forEach((name) => seen.add(name));
    const collect = (records: MutationRecord[]) => {
      for (const mutation of records) {
        for (const added of mutation.addedNodes) {
          if (!(added instanceof HTMLElement)) continue;
          const found = added.matches("button") ? [added] : [...added.querySelectorAll("button")];
          for (const button of found) seen.add(button.textContent ?? "");
        }
      }
    };
    const observer = new MutationObserver(collect);
    observer.observe(document.body, { childList: true, subtree: true });
    return () => {
      collect(observer.takeRecords());
      observer.disconnect();
      record();
      return seen;
    };
  }

  test("an item re-parked on another node offers nothing until that node has been read", async () => {
    mockDialogServices();
    const staged = stageTwoNodes();
    const { rerender } = await renderDialog(onHumanNode());
    await staged.answer(0, "n-a");
    expectButtons(["Approve", "Reject"]);

    const everShown = watchButtons();
    const asked = staged.reads.length;
    rerender(dialogFor(onPrNode()));
    await settled();

    expect(staged.reads.length).toBeGreaterThan(asked);
    expectButtons(["Merge"]);

    await staged.answer(asked, "n-b");

    expectButtons(["Approve", "Merge", "Reject"]);
    const shown = everShown();
    for (const name of RESERVED_BUTTONS) expect(shown.has(name), name).toBe(false);
  });

  test("the same item fetched again offers nothing until its run has been read again, at the cost of that read only", async () => {
    mockDialogServices();
    const staged = stageTwoNodes();
    const { rerender } = await renderDialog(onHumanNode());
    await staged.answer(0, "n-a");
    expectButtons(["Approve", "Reject"]);

    const asked = staged.reads.length;
    rerender(dialogFor(onHumanNode()));
    await settled();

    expect(staged.reads.length).toBeGreaterThan(asked);
    expectButtons([]);

    await staged.answer(asked, "n-a");

    expectButtons(["Approve", "Reject"]);
    expect(staged.getVersionGraph).toHaveBeenCalledTimes(1);
    expect(staged.getNodeOutputs).toHaveBeenCalledTimes(1);
  });

  test("a run read answered late for an earlier state of the item changes nothing", async () => {
    mockDialogServices();
    const staged = stageTwoNodes();
    const { rerender } = await renderDialog(onHumanNode());
    const forHumanNode = staged.reads.length;
    expect(forHumanNode).toBeGreaterThan(0);

    rerender(dialogFor(onPrNode()));
    await settled();
    for (const read of staged.reads.slice(forHumanNode)) read.resolve(parkedRun("n-b"));
    await settled();
    expectButtons(["Approve", "Merge", "Reject"]);

    const everShown = watchButtons();
    for (const read of staged.reads.slice(0, forHumanNode)) read.resolve(parkedRun("n-a"));
    await settled();

    expectButtons(["Approve", "Merge", "Reject"]);
    const shown = everShown();
    for (const name of RESERVED_BUTTONS) expect(shown.has(name), name).toBe(false);
  });

  test("the queued-writes refresh of the run, naming another node, changes nothing", async () => {
    const { emit } = mockDialogServices();
    const staged = stageTwoNodes();
    await renderDialog(onPrNode());
    await staged.answer(0, "n-b");
    expectButtons(["Approve", "Merge", "Reject"]);

    const everShown = watchButtons();
    const asked = staged.reads.length;
    await act(async () => {
      emit("PrQueueChanged", { runId: "run-1" });
    });
    expect(staged.reads.length).toBeGreaterThan(asked);
    await staged.answer(asked, "n-a");

    expectButtons(["Approve", "Merge", "Reject"]);
    const shown = everShown();
    for (const name of RESERVED_BUTTONS) expect(shown.has(name), name).toBe(false);
  });

  test("a failed refresh of the run leaves the buttons and raises nothing", async () => {
    const { emit } = mockDialogServices();
    const staged = stageTwoNodes();
    await renderDialog(onPrNode());
    await staged.answer(0, "n-b");

    const asked = staged.reads.length;
    await act(async () => {
      emit("PrQueueChanged", { runId: "run-1" });
    });
    for (const read of staged.reads.slice(asked)) read.reject({ status: 503, message: "down" });
    await settled();

    expectButtons(["Approve", "Merge", "Reject"]);
    expect(pane().textContent).not.toMatch(LOAD_ERROR);
  });
});
