import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, act, waitFor } from "@testing-library/react";
import EditPanel from "./EditPanel";
import { WorkItem, WorkItemStatus, Repository } from "../../types";
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
    priority: 0,
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

describe("EditPanel priority", () => {
  test("the form has no priority field and a save does not send one", async () => {
    const workItem = makeWorkItem({ priority: 3 });
    vi.spyOn(authServices.workItemService, "checkBranchName").mockResolvedValue({
      error: null,
      warning: null,
    });
    const update = vi
      .spyOn(authServices.workItemService, "update")
      .mockResolvedValue(makeWorkItem({ title: "Renamed", priority: 3 }));
    const onDone = vi.fn();

    render(
      <EditPanel workItem={workItem} detail={makeDetail()} onSave={vi.fn()} onDone={onDone} />,
    );

    expect(screen.queryByLabelText(/priority/i)).toBeNull();
    expect(screen.queryByText(/priority/i)).toBeNull();

    fireEvent.change(screen.getByLabelText("Title"), { target: { value: "Renamed" } });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Update" }));
    });

    await waitFor(() => expect(onDone).toHaveBeenCalled());
    expect(update).toHaveBeenCalledTimes(1);
    expect(update.mock.calls[0][1]).toMatchObject({ title: "Renamed" });
    expect(update.mock.calls[0][1]).not.toHaveProperty("priority");
  });
});
