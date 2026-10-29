import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, screen, within, fireEvent } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { PreviewPanel } from "./panels";
import { WorkItem, WorkItemStatus } from "../../types";
import type { WorkItemDetail } from "./useWorkItemDetail";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const workItem = {
  id: "wi-1",
  title: "Test",
  description: "",
  status: WorkItemStatus.Running,
  priority: 0,
  tags: [],
  repositoryId: "repo-a",
  worktreePath: "/tmp/wt",
  dependencyIds: [],
  dependentIds: [],
} as unknown as WorkItem;

const service = (name: string, status: string, extra: Record<string, unknown> = {}) => ({
  name,
  portAlias: name,
  status,
  port: 5000,
  suggestedPort: 5000,
  healthUrl: null,
  publicUrl: null,
  logFilePath: null,
  processId: null,
  exitCode: null,
  error: null,
  ...extra,
});

function renderPanel(
  state: string,
  services: ReturnType<typeof service>[],
  extra: Record<string, unknown> = {},
  detailExtra: Record<string, unknown> = {},
) {
  const detail = {
    preview: {
      configured: true,
      state,
      worktreePath: "/tmp/wt",
      configPath: null,
      profileName: "app",
      publicHost: null,
      stateDirectory: null,
      message: null,
      install: null,
      services,
      ...extra,
    },
    previewLoading: false,
    previewStarting: false,
    previewError: null,
    repositories: [],
    reloadRepositories: async () => {},
    refreshPreview: async () => {},
    fetchPreviewLogs: vi.fn(async () => "boom: cannot bind"),
    handleStartPreview: async () => {},
    handleStopPreview: async () => {},
    handleStartService: async () => {},
    handleStopService: async () => {},
    ...detailExtra,
  } as unknown as WorkItemDetail;
  render(
    <MemoryRouter>
      <PreviewPanel workItem={workItem} detail={detail} />
    </MemoryRouter>,
  );
  return detail;
}

const row = (name: string) =>
  screen.getByText(name, { selector: ".preview-cell-name" }).closest("tr")!;

describe("PreviewPanel while a start is in progress", () => {
  test("shows the install step the start is on, with its command", () => {
    renderPanel("installing", [service("web", "pending")], {
      install: { index: 2, total: 3, command: "npm ci" },
    });

    expect(screen.getByText("Installing", { selector: ".pill" })).toBeTruthy();
    expect(screen.getByText(/step 2 of 3/i).textContent).toContain("npm ci");
  });

  test("each service row names its own stage, and Failed shows exit code, error and log", () => {
    renderPanel("starting", [
      service("a", "pending"),
      service("b", "starting"),
      service("c", "waitingForHealth"),
      service("d", "running"),
      service("e", "failed", { exitCode: 3, error: "Preview service 'e' exited with code 3" }),
    ]);

    expect(screen.getByText("Starting", { selector: ".pill-dot" })).toBeTruthy();
    expect(within(row("a")).getByText("Pending")).toBeTruthy();
    expect(within(row("b")).getByText("Starting")).toBeTruthy();
    expect(within(row("c")).getByText("Waiting for health")).toBeTruthy();
    expect(within(row("d")).getByText("Running")).toBeTruthy();
    const failed = row("e");
    expect(within(failed).getByText("Failed")).toBeTruthy();
    expect(failed.textContent).toContain("exit 3");
    expect(failed.textContent).toContain("exited with code 3");
    expect(within(failed).getByRole("button", { name: "Log" })).toBeTruthy();
  });

  test("hides Start Preview and locks the row controls until the start settles", () => {
    renderPanel("starting", [service("a", "pending"), service("b", "running")]);

    expect(screen.queryByRole("button", { name: "Start Preview" })).toBeNull();
    for (const name of ["a", "b"]) {
      const buttons = within(row(name)).getAllByRole("button");
      const startStop = buttons.find((b) => b.textContent === "Start" || b.textContent === "Stop")!;
      expect((startStop as HTMLButtonElement).disabled).toBe(true);
    }
    expect((screen.getByLabelText("Port for a") as HTMLInputElement).readOnly).toBe(true);
  });

  test("a user's own start in flight also locks the row buttons", () => {
    renderPanel("stopped", [service("a", "stopped")], {}, { previewStarting: true });

    const start = within(row("a")).getByRole("button", { name: "Start" }) as HTMLButtonElement;
    expect(start.disabled).toBe(true);
  });

  test("a failed start leaves Start Preview and the row controls usable again", () => {
    renderPanel("failed", [
      service("a", "stopped"),
      service("b", "failed", { exitCode: 1, error: "exited" }),
    ]);

    expect(screen.getByRole("button", { name: "Start Preview" })).toBeTruthy();
    const start = within(row("a")).getByRole("button", { name: "Start" }) as HTMLButtonElement;
    expect(start.disabled).toBe(false);
    fireEvent.click(within(row("b")).getByRole("button", { name: "Log" }));
  });
});
