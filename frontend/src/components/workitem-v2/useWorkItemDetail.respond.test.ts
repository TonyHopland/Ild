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

    const { result } = renderHook(() => useWorkItemDetail(makeParkedWorkItem(), vi.fn()));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());
    await act(async () => {
      result.current.setFeedbackInput("Looks good");
    });

    // Both presses land before React can render the buttons disabled, which is
    // what an impatient second click looks like.
    await act(async () => {
      await Promise.all([result.current.handleApprove(), result.current.handleApprove()]);
    });

    expect(answer).toHaveBeenCalledTimes(1);
    expect(answer.mock.calls[0][1]).toBe("Looks good");
    expect(upload).not.toHaveBeenCalled();
  });

  test("a second answer is allowed once the first has finished", async () => {
    stubServices();
    const answer = vi.spyOn(workItemService, "humanFeedbackInput").mockResolvedValue(undefined);

    const { result } = renderHook(() => useWorkItemDetail(makeParkedWorkItem(), vi.fn()));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());

    await act(async () => await result.current.handleApprove());
    await act(async () => await result.current.handleApprove());

    expect(answer).toHaveBeenCalledTimes(2);
    expect(result.current.respondLoading).toBe(false);
  });

  test("a refused answer releases the buttons so it can be retried", async () => {
    stubServices();
    const answer = vi
      .spyOn(workItemService, "humanFeedbackInput")
      .mockRejectedValue({ status: 400, message: "Input is too long." });

    const { result } = renderHook(() => useWorkItemDetail(makeParkedWorkItem(), vi.fn()));
    await waitFor(() => expect(result.current.editAttachments.limits).not.toBeNull());

    await act(async () => await result.current.handleApprove());

    expect(answer).toHaveBeenCalledTimes(1);
    expect(result.current.respondError).toBe("Input is too long.");
    expect(result.current.respondLoading).toBe(false);
  });
});
