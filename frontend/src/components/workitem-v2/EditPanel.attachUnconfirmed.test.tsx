import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act } from "@testing-library/react";
import EditPanel from "./EditPanel";
import { WorkItem, WorkItemStatus, WorkItemPriority, Repository } from "../../types";
import * as authServices from "../../services/auth";
import type { WorkItemDetail } from "./useWorkItemDetail";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.useRealTimers();
});

const ON_ORIGIN = "feature/shared";

function repo(id: string): Repository {
  return {
    id,
    name: id,
    remoteProviderId: "rp-1",
    cloneUrl: `https://example.com/o/${id}.git`,
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: WorkItemStatus.Backlog,
    createdAt: "2025-01-01T00:00:00Z",
  };
}

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
  return {
    id: "wi-1",
    title: "Test",
    description: "",
    status: WorkItemStatus.Backlog,
    priority: WorkItemPriority.Medium,
    tags: [],
    loopTemplateId: "tmpl-1",
    loopTemplateVersion: "v1",
    repositoryId: "repo-a",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: null,
    dependencyIds: [],
    dependentIds: [],
    worktreePath: null,
    branchName: null,
    ...overrides,
  };
}

function makeDetail(): WorkItemDetail {
  return {
    repositories: [repo("repo-a"), repo("repo-b")],
    templates: [],
    aiProviders: [],
    whileBusy: async (fn: () => Promise<void>) => fn(),
    editAttachments: {
      staged: [],
      hasPending: () => false,
      handlePaste: () => {},
      uploadAll: async () => ({ ok: true, abandoned: false, errors: [] }),
    },
  } as unknown as WorkItemDetail;
}

const attachBox = () => screen.queryByRole("checkbox", { name: /attach to existing branch/i });

async function settleCheck() {
  await act(async () => {
    vi.advanceTimersByTime(400);
    await Promise.resolve();
    await Promise.resolve();
  });
}

describe("EditPanel attach to existing branch, unconfirmed", () => {
  test("a stored tick the check could not confirm is left alone rather than saved again", async () => {
    vi.spyOn(authServices.workItemService, "checkBranchName").mockRejectedValue(
      new Error("network"),
    );
    const update = vi
      .spyOn(authServices.workItemService, "update")
      .mockImplementation(async (_id, data) => makeWorkItem(data as Partial<WorkItem>));
    vi.useFakeTimers();
    render(
      <EditPanel
        workItem={makeWorkItem({ branchNameOverride: ON_ORIGIN, attachToExistingBranch: true })}
        detail={makeDetail()}
        onSave={vi.fn()}
        onDone={vi.fn()}
      />,
    );
    await settleCheck();
    expect(attachBox()).toBeNull();

    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Renamed" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Update" }));
      await Promise.resolve();
    });

    const payload = update.mock.calls[0][1] as Record<string, unknown>;
    expect(payload.title).toBe("Renamed");
    expect(payload.attachToExistingBranch).toBeUndefined();
  });

  test("an answer about another repository does not offer the checkbox", async () => {
    let resolveSecond: (v: {
      error: null;
      warning: null;
      existsOnOrigin: boolean;
    }) => void = () => {};
    vi.spyOn(authServices.workItemService, "checkBranchName")
      .mockResolvedValueOnce({ error: null, warning: null, existsOnOrigin: true })
      .mockImplementationOnce(() => new Promise((r) => (resolveSecond = r)));
    vi.useFakeTimers();
    render(
      <EditPanel
        workItem={makeWorkItem({ branchNameOverride: ON_ORIGIN })}
        detail={makeDetail()}
        onSave={vi.fn()}
        onDone={vi.fn()}
      />,
    );
    await settleCheck();
    expect(attachBox()).not.toBeNull();

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Repository"), { target: { value: "repo-b" } });
      await Promise.resolve();
    });
    expect(attachBox()).toBeNull();

    await settleCheck();
    await act(async () => {
      resolveSecond({ error: null, warning: null, existsOnOrigin: true });
      await Promise.resolve();
    });
    expect(attachBox()).not.toBeNull();
  });
});
