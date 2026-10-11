import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";
import { useWorkItemDetail } from "./useWorkItemDetail";
import { WorkItem, WorkItemStatus, WorkItemPriority } from "../../types";
import * as signalRHook from "../../hooks/useSignalR";
import {
  repositoryService,
  loopTemplateService,
  workItemService,
  aiProviderService,
  settingsService,
} from "../../services/auth";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function makeParkedWorkItem(): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.HumanFeedback,
    priority: WorkItemPriority.Medium,
    tags: [],
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
    latestLoopRunId: "run-1",
    dependencyIds: [],
    dependentIds: [],
  };
}

function stubServices() {
  vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn().mockResolvedValue(undefined),
    connectionState: "connected",
  } as unknown as ReturnType<typeof signalRHook.useSignalR>);
  vi.spyOn(repositoryService, "getAll").mockResolvedValue([]);
  vi.spyOn(loopTemplateService, "getAll").mockResolvedValue([]);
  vi.spyOn(aiProviderService, "getAll").mockResolvedValue([]);
  vi.spyOn(workItemService, "getRuns").mockResolvedValue([]);
  vi.spyOn(workItemService, "getDependencies").mockResolvedValue([]);
  vi.spyOn(workItemService, "getAll").mockResolvedValue([]);
  vi.spyOn(workItemService, "getById").mockResolvedValue(makeParkedWorkItem());
  vi.spyOn(settingsService, "getAttachmentLimits").mockResolvedValue({
    maxBytesPerFile: 25 * 1024 * 1024,
    maxFilesPerRequest: 10,
    maxTotalBytesPerWorkItem: 250 * 1024 * 1024,
  });
}

describe("answering a parked run", () => {
  test("answering twice in one go sends the answer once", async () => {
    stubServices();
    const upload = vi.spyOn(workItemService, "uploadAttachment").mockResolvedValue([]);
    const answer = vi.spyOn(workItemService, "humanFeedbackInput").mockResolvedValue(undefined);

    const workItem = makeParkedWorkItem();
    const { result } = renderHook(() => useWorkItemDetail(workItem, vi.fn()));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());
    await act(async () => {
      result.current.setReplyInput("run-1", "Looks good");
    });

    // Both presses land before React can render the buttons disabled, which is
    // what an impatient second click looks like.
    await act(async () => {
      await Promise.all([
        result.current.handleApprove("run-1"),
        result.current.handleApprove("run-1"),
      ]);
    });

    expect(answer).toHaveBeenCalledTimes(1);
    expect(answer.mock.calls[0]).toEqual(["wi-1", "run-1", "Looks good"]);
    expect(upload).not.toHaveBeenCalled();
  });

  test("a second answer is allowed once the first has finished", async () => {
    stubServices();
    const answer = vi.spyOn(workItemService, "humanFeedbackInput").mockResolvedValue(undefined);

    const workItem = makeParkedWorkItem();
    const { result } = renderHook(() => useWorkItemDetail(workItem, vi.fn()));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());

    await act(async () => await result.current.handleApprove("run-1"));
    await act(async () => await result.current.handleApprove("run-1"));

    expect(answer).toHaveBeenCalledTimes(2);
    expect(result.current.replyFor("run-1").loading).toBe(false);
  });

  test("a refused answer releases the buttons so it can be retried", async () => {
    stubServices();
    const answer = vi
      .spyOn(workItemService, "humanFeedbackInput")
      .mockRejectedValue({ status: 400, message: "Input is too long." });

    const workItem = makeParkedWorkItem();
    const { result } = renderHook(() => useWorkItemDetail(workItem, vi.fn()));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());

    await act(async () => await result.current.handleApprove("run-1"));

    expect(answer).toHaveBeenCalledTimes(1);
    expect(result.current.replyFor("run-1").error).toBe("Input is too long.");
    expect(result.current.replyFor("run-1").loading).toBe(false);
  });
});

const MOVED_ON = { status: 409, message: "The run is not waiting for an answer." };

type Answer = "approve" | "reject" | "edge";

function refuse(answer: Answer, error: unknown) {
  if (answer === "approve")
    return vi.spyOn(workItemService, "humanFeedbackInput").mockRejectedValue(error);
  if (answer === "reject")
    return vi.spyOn(workItemService, "humanFeedbackReject").mockRejectedValue(error);
  return vi.spyOn(workItemService, "humanFeedbackEdge").mockRejectedValue(error);
}

function press(detail: ReturnType<typeof useWorkItemDetail>, answer: Answer) {
  if (answer === "approve") return detail.handleApprove("run-1");
  if (answer === "reject") return detail.handleReject("run-1");
  return detail.handleEdge("run-1", "on_merged");
}

describe("an answer sent after the run has moved on", () => {
  test.each<Answer>(["approve", "reject", "edge"])(
    "a 409 on %s re-reads the item and keeps the typed text aside, out of the box",
    async (answer) => {
      stubServices();
      const running = {
        ...makeParkedWorkItem(),
        status: WorkItemStatus.Running,
        humanFeedbackReason: null,
      };
      const getById = vi.spyOn(workItemService, "getById").mockResolvedValue(running);
      const sent = refuse(answer, MOVED_ON);
      const onSave = vi.fn();

      const workItem = makeParkedWorkItem();
      const { result } = renderHook(() => useWorkItemDetail(workItem, onSave));
      await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());
      await act(async () => {
        result.current.setReplyInput("run-1", "Please also fix the flaky test");
      });
      getById.mockClear();

      await act(async () => await press(result.current, answer));

      expect(sent).toHaveBeenCalledTimes(1);
      expect(getById).toHaveBeenCalledWith("wi-1");
      expect(onSave).toHaveBeenCalledWith(running);
      expect(result.current.movedOn).toEqual({ text: "Please also fix the flaky test" });
      expect(result.current.replyFor("run-1").input).toBe("");
      expect(result.current.replyFor("run-1").loading).toBe(false);
    },
  );

  test.each([
    ["a 500", { status: 500, message: "Something broke on the server." }],
    ["a 400", { status: 400, message: "Input is too long." }],
    ["a network failure", { message: "Failed to fetch" }],
  ])("%s is shown in the box with the text kept, and nothing is set aside", async (_, error) => {
    stubServices();
    refuse("approve", error);

    const workItem = makeParkedWorkItem();
    const onSave = vi.fn();
    const { result } = renderHook(() => useWorkItemDetail(workItem, onSave));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());
    await act(async () => {
      result.current.setReplyInput("run-1", "Looks good");
    });

    await act(async () => await result.current.handleApprove("run-1"));

    expect(result.current.movedOn).toBeNull();
    expect(result.current.replyFor("run-1").error).toBe(error.message);
    expect(result.current.replyFor("run-1").input).toBe("Looks good");
    expect(result.current.replyFor("run-1").loading).toBe(false);
  });

  test("a 409 that lands after the dialog has switched to another item sets nothing aside there", async () => {
    stubServices();
    let refuseAnswer!: (error: unknown) => void;
    vi.spyOn(workItemService, "humanFeedbackInput").mockReturnValue(
      new Promise<void>((_, reject) => {
        refuseAnswer = reject;
      }),
    );
    const first = makeParkedWorkItem();
    const other = { ...makeParkedWorkItem(), id: "wi-2", title: "Other" };
    const onSave = vi.fn();

    // The board keys the dialog by item, so switching items unmounts this
    // instance and builds a fresh one for the other item.
    const { result, unmount } = renderHook(() => useWorkItemDetail(first, onSave));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());
    await act(async () => {
      result.current.setReplyInput("run-1", "Meant for wi-1");
    });
    let answered!: Promise<void>;
    act(() => {
      answered = result.current.handleApprove("run-1");
    });

    unmount();
    const { result: switched } = renderHook(() => useWorkItemDetail(other, onSave));
    await waitFor(() => expect(switched.current.editAttachments.limits).not.toBeNull());
    await act(async () => {
      refuseAnswer(MOVED_ON);
      await answered;
    });

    expect(switched.current.movedOn).toBeNull();
  });
});

describe("a refused answer whose re-read fails", () => {
  test("keeps the controls held, so the stale box cannot send again", async () => {
    stubServices();
    const sent = refuse("approve", MOVED_ON);
    vi.spyOn(workItemService, "getById").mockRejectedValue({ message: "Failed to fetch" });

    const workItem = makeParkedWorkItem();
    const { result } = renderHook(() => useWorkItemDetail(workItem, vi.fn()));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());
    await act(async () => {
      result.current.setReplyInput("run-1", "Please also fix the flaky test");
    });

    await act(async () => await result.current.handleApprove("run-1"));
    await act(async () => await result.current.handleApprove("run-1"));

    expect(sent).toHaveBeenCalledTimes(1);
    expect(result.current.movedOn).toEqual({ text: "Please also fix the flaky test" });
    expect(result.current.replyFor("run-1").error).toContain("could not be refreshed");
    expect(result.current.replyFor("run-1").loading).toBe(true);
  });
});
