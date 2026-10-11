import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import EditPanel from "./EditPanel";
import {
  WorkItem,
  WorkItemStatus,
  WorkItemPriority,
  Repository,
  BranchNameCheck,
} from "../../types";
import * as authServices from "../../services/auth";
import type { WorkItemDetail } from "./useWorkItemDetail";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function repo(id: string, name: string): Repository {
  return {
    id,
    name,
    remoteProviderId: "rp-1",
    cloneUrl: `https://example.com/o/${name}.git`,
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
    repositories: [repo("repo-a", "alpha"), repo("repo-b", "beta")],
    templates: [],
    aiProviders: [],
    whileBusy: async (fn: () => Promise<void>) => fn(),
    editAttachments: {
      staged: [],
      limits: null,
      stagingError: null,
      uploading: false,
      add: () => {},
      remove: () => {},
      clear: () => {},
      hasPending: () => false,
      forgetUploaded: () => {},
      handlePaste: () => {},
      uploadAll: async () => ({ ok: true, abandoned: false, errors: [] }),
    },
  } as unknown as WorkItemDetail;
}

describe("EditPanel repository", () => {
  test("an existing item's repository can be changed for its next run and is saved", async () => {
    const workItem = makeWorkItem();
    const saved = makeWorkItem({ repositoryId: "repo-b" });
    vi.spyOn(authServices.workItemService, "checkBranchName").mockResolvedValue({
      error: null,
      warning: null,
    });
    const update = vi.spyOn(authServices.workItemService, "update").mockResolvedValue(saved);
    const onSave = vi.fn();
    const onDone = vi.fn();

    render(<EditPanel workItem={workItem} detail={makeDetail()} onSave={onSave} onDone={onDone} />);

    const select = screen.getByLabelText("Repository") as HTMLSelectElement;
    expect(select.disabled).toBe(false);
    const hintId = select.getAttribute("aria-describedby");
    expect(hintId).toBeTruthy();
    expect(document.getElementById(hintId!)?.textContent).toMatch(/next run/i);

    fireEvent.change(select, { target: { value: "repo-b" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Update" }));
    });

    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(update).toHaveBeenCalledWith(
      "wi-1",
      expect.objectContaining({ repositoryId: "repo-b" }),
    );
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ repositoryId: "repo-b" }));
  });
});

describe("EditPanel attach to existing branch", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  const ON_ORIGIN = "feature/shared";
  const NOT_ON_ORIGIN = "feature/fresh";
  const UNREACHABLE = "feature/unreachable";

  function setup(workItem: WorkItem = makeWorkItem()) {
    const check = vi
      .spyOn(authServices.workItemService, "checkBranchName")
      .mockImplementation(async (name: string) => {
        if (name === UNREACHABLE) throw new Error("network");
        return {
          error: null,
          warning: null,
          existsOnOrigin: name === ON_ORIGIN,
        } as BranchNameCheck;
      });
    const update = vi
      .spyOn(authServices.workItemService, "update")
      .mockImplementation(async (_id, data) => makeWorkItem(data as Partial<WorkItem>));
    vi.useFakeTimers();
    render(
      <EditPanel workItem={workItem} detail={makeDetail()} onSave={vi.fn()} onDone={vi.fn()} />,
    );
    return { check, update };
  }

  const attachBox = () =>
    screen.queryByRole("checkbox", {
      name: /attach to existing branch/i,
    }) as HTMLInputElement | null;

  async function typeBranchName(value: string) {
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Branch name (optional)"), { target: { value } });
      await Promise.resolve();
    });
  }

  async function settleCheck() {
    await act(async () => {
      vi.advanceTimersByTime(400);
      await Promise.resolve();
      await Promise.resolve();
    });
  }

  async function saveAndReadPayload(update: ReturnType<typeof setup>["update"]) {
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Update" }));
      await Promise.resolve();
    });
    expect(update).toHaveBeenCalledTimes(1);
    return update.mock.calls[0][1] as Record<string, unknown>;
  }

  test("offers the checkbox, with a warning about someone else's branch, once the check finds the name on origin", async () => {
    const { check } = setup();
    expect(attachBox()).toBeNull();

    await typeBranchName(ON_ORIGIN);
    expect(attachBox()).toBeNull();
    expect(document.body.textContent).not.toMatch(/someone else/i);

    await settleCheck();
    expect(check.mock.calls[check.mock.calls.length - 1][0]).toBe(ON_ORIGIN);
    expect(attachBox()).not.toBeNull();
    expect(attachBox()!.checked).toBe(false);
    expect(document.body.textContent).toMatch(/someone else/i);
  });

  test("ticking it and saving sends attachToExistingBranch true", async () => {
    const { update } = setup();
    await typeBranchName(ON_ORIGIN);
    await settleCheck();

    fireEvent.click(attachBox()!);
    expect(attachBox()!.checked).toBe(true);

    expect(await saveAndReadPayload(update)).toMatchObject({
      branchNameOverride: ON_ORIGIN,
      attachToExistingBranch: true,
    });
  });

  test("renaming a ticked branch to one not on origin removes the checkbox and saves false", async () => {
    const { update } = setup();
    await typeBranchName(ON_ORIGIN);
    await settleCheck();
    fireEvent.click(attachBox()!);

    await typeBranchName(NOT_ON_ORIGIN);
    // The completed check was for the old name, so it says nothing about this one.
    expect(attachBox()).toBeNull();
    await settleCheck();
    expect(attachBox()).toBeNull();

    expect(await saveAndReadPayload(update)).toMatchObject({
      branchNameOverride: NOT_ON_ORIGIN,
      attachToExistingBranch: false,
    });
  });

  test("clearing the name of a ticked branch removes the checkbox and saves false", async () => {
    const { update } = setup();
    await typeBranchName(ON_ORIGIN);
    await settleCheck();
    fireEvent.click(attachBox()!);

    await typeBranchName("");
    expect(attachBox()).toBeNull();
    await settleCheck();
    expect(attachBox()).toBeNull();

    expect(await saveAndReadPayload(update)).toMatchObject({
      branchNameOverride: "",
      attachToExistingBranch: false,
    });
  });

  test("a failed check hides the checkbox without clearing the tick", async () => {
    setup();
    await typeBranchName(ON_ORIGIN);
    await settleCheck();
    fireEvent.click(attachBox()!);

    await typeBranchName(UNREACHABLE);
    await settleCheck();
    expect(attachBox()).toBeNull();

    await typeBranchName(ON_ORIGIN);
    await settleCheck();
    expect(attachBox()).not.toBeNull();
    expect(attachBox()!.checked).toBe(true);
  });

  test("an item saved attached to a branch on origin shows it ticked once the check completes", async () => {
    setup(
      makeWorkItem({
        branchNameOverride: ON_ORIGIN,
        attachToExistingBranch: true,
      } as Partial<WorkItem>),
    );
    expect(attachBox()).toBeNull();

    await settleCheck();
    expect(attachBox()).not.toBeNull();
    expect(attachBox()!.checked).toBe(true);
  });
});
