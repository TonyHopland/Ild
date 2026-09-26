import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, cleanup } from "@testing-library/react";
import { PreviewPanel } from "./panels";
import { WorkItem, WorkItemStatus, WorkItemPriority, Repository } from "../../types";
import type { WorkItemDetail } from "./useWorkItemDetail";

afterEach(() => cleanup());

function repo(id: string, name: string): Repository {
  return {
    id,
    name,
    remoteProviderId: "rp-1",
    cloneUrl: `https://example.com/o/${name}.git`,
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: WorkItemStatus.Backlog,
    hasPreviewEnv: false,
    createdAt: "2025-01-01T00:00:00Z",
  };
}

function makeWorkItem(overrides: Partial<WorkItem> = {}): WorkItem {
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
    repositoryId: "repo-a",
    prUrl: null,
    pullRequestBranch: null,
    humanFeedbackReason: null,
    humanFeedbackActions: null,
    createdAt: "2025-01-01T00:00:00Z",
    startedAt: null,
    completedAt: null,
    currentLoopRunId: "run-1",
    dependencyIds: [],
    dependentIds: [],
    worktreePath: "/tmp/wt",
    branchName: "ild/wi-1",
    ...overrides,
  };
}

function makeDetail(): WorkItemDetail {
  return {
    preview: null,
    previewLoading: false,
    previewError: null,
    repositories: [repo("repo-a", "alpha"), repo("repo-b", "beta")],
    reloadRepositories: async () => {},
    handleStartPreview: async () => {},
    handleStopPreview: async () => {},
  } as unknown as WorkItemDetail;
}

describe("PreviewPanel custom .env", () => {
  test("edits the env of the repository the current run was created on, not the item's edited one", () => {
    // The item was re-pointed at beta after its run started on alpha; the
    // preview runs in that run's worktree, so alpha's env is what it gets.
    const { container } = render(
      <PreviewPanel
        workItem={makeWorkItem({ repositoryId: "repo-b", runRepositoryId: "repo-a" })}
        detail={makeDetail()}
      />,
    );

    const warning = container.querySelector(".preview-env-warning");
    expect(warning?.textContent).toContain("alpha");
    expect(warning?.textContent).not.toContain("beta");
  });
});
