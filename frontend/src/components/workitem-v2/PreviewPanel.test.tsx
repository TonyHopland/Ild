import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, cleanup, screen, fireEvent, act, within, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { PreviewPanel } from "./panels";
import { WorkItem, WorkItemStatus, Repository, WorktreePreviewService } from "../../types";
import { useWorkItemDetail, type WorkItemDetail } from "./useWorkItemDetail";
import * as signalRHook from "../../hooks/useSignalR";
import * as authServices from "../../services/auth";
import { WorkItemHubProvider } from "../../hooks/useWorkItemHub";

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

describe("PreviewPanel controls", () => {
  const service = (status: string): WorktreePreviewService => ({
    name: "web",
    portAlias: "web",
    status,
    port: status === "stopped" ? null : 5173,
    suggestedPort: null,
    healthUrl: null,
    publicUrl: null,
    logFilePath: null,
    processId: status === "stopped" ? null : 42,
    exitCode: status === "exited" ? 1 : null,
  });

  function renderWith(status: string, state: string) {
    const detail = {
      ...makeDetail(),
      preview: {
        configured: true,
        state,
        worktreePath: "/tmp/wt",
        configPath: null,
        profileName: "app",
        publicHost: null,
        stateDirectory: null,
        message: null,
        services: [service(status)],
      },
    } as unknown as WorkItemDetail;
    render(<PreviewPanel workItem={makeWorkItem()} detail={detail} />);
  }

  test("a preview whose services all exited can be stopped as well as started again", () => {
    renderWith("exited", "failed");

    expect(screen.getByRole("button", { name: "Stop Preview" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Start Preview" })).toBeTruthy();
  });

  test("a running preview offers Stop and a stopped one only Start", () => {
    renderWith("running", "running");
    expect(screen.getByRole("button", { name: "Stop Preview" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Start Preview" })).toBeNull();
    cleanup();

    renderWith("stopped", "stopped");
    expect(screen.queryByRole("button", { name: "Stop Preview" })).toBeNull();
    expect(screen.getByRole("button", { name: "Start Preview" })).toBeTruthy();
  });
});

describe("PreviewPanel against the services", () => {
  function mockServices() {
    vi.spyOn(signalRHook, "useSignalR").mockReturnValue({
      on: vi.fn(),
      off: vi.fn(),
      invoke: vi.fn(),
      connectionState: "connected",
    });
    vi.spyOn(authServices.repositoryService, "getAll").mockResolvedValue([
      { ...repo("repo-1", "my-repo"), hasPreviewEnv: false },
    ]);
    vi.spyOn(authServices.loopTemplateService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.aiProviderService, "getAll").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getRuns").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getDependencies").mockResolvedValue([]);
    vi.spyOn(authServices.workItemService, "getAll").mockResolvedValue([]);
  }

  function Harness({ workItem }: { workItem: WorkItem }) {
    const detail = useWorkItemDetail(workItem, vi.fn());
    return <PreviewPanel workItem={workItem} detail={detail} />;
  }

  async function renderPreview() {
    const workItem = makeWorkItem({ repositoryId: "repo-1", worktreePath: "/wt" });
    await act(async () => {
      render(
        <MemoryRouter>
          <Harness workItem={workItem} />
        </MemoryRouter>,
        { wrapper: WorkItemHubProvider },
      );
      await Promise.resolve();
    });
  }

  // jsdom renders a collapsed <details> body but does not toggle it from a summary
  // click, so drive the open state (and the toggle event the editor loads on) by hand.
  async function openEnvEditor() {
    const details = document.querySelector("details.preview-env") as HTMLDetailsElement;
    await act(async () => {
      details.open = true;
      fireEvent(details, new Event("toggle"));
      await Promise.resolve();
    });
  }

  test("preview tab renders one table row per service with state, name, port and link", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "running",
      worktreePath: "/wt",
      configPath: "/wt/ild.config.json",
      profileName: "app",
      publicHost: "127.0.0.1",
      stateDirectory: "/state",
      message: null,
      services: [
        {
          name: "web",
          portAlias: "web",
          status: "running",
          port: 5173,
          suggestedPort: 5173,
          healthUrl: "http://127.0.0.1:5173/",
          publicUrl: "http://127.0.0.1:5173",
          logFilePath: "/state/web.log",
          processId: 42,
          exitCode: null,
        },
        {
          name: "api",
          portAlias: "api",
          status: "exited",
          port: 8080,
          suggestedPort: 8080,
          healthUrl: "http://127.0.0.1:8080/health",
          publicUrl: null,
          logFilePath: "/state/api.log",
          processId: 43,
          exitCode: 1,
        },
      ],
    });
    await renderPreview();

    const table = await screen.findByRole("table");
    const utils = within(table);
    expect(utils.getByText("State")).toBeTruthy();
    expect(utils.getByText("Link")).toBeTruthy();

    expect(utils.getByText("web")).toBeTruthy();
    expect(utils.getByText("Running")).toBeTruthy();
    const link = utils.getByRole("link", { name: "http://127.0.0.1:5173" });
    expect(link.getAttribute("href")).toBe("http://127.0.0.1:5173");

    expect(utils.getByText("api")).toBeTruthy();
    expect(utils.getByText("Error")).toBeTruthy();
    expect(utils.getByText(/exit 1/)).toBeTruthy();
  });

  test("preview tab exposes a repo-wide custom .env editor with a persistence warning", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "stopped",
      worktreePath: "/wt",
      configPath: null,
      profileName: null,
      publicHost: null,
      stateDirectory: null,
      message: null,
      services: [],
    });
    const updateSpy = vi.spyOn(authServices.repositoryService, "update").mockResolvedValue({
      id: "repo-1",
      name: "my-repo",
      remoteProviderId: "rp-1",
      cloneUrl: "",
      defaultBranch: null,
      worktreesPath: null,
      defaultIntakeStatus: WorkItemStatus.Backlog,
      hasPreviewEnv: true,
      createdAt: "2025-01-01T00:00:00Z",
    });

    const envSpy = vi
      .spyOn(authServices.repositoryService, "getPreviewEnv")
      .mockResolvedValue("STORED=1");

    await renderPreview();

    const warning = await screen.findByRole("note");
    expect(warning.textContent).toMatch(/Repository-wide/);
    expect(warning.textContent).toMatch(/every.*work item that uses it, not just this one/);

    // Egress out of the container is dropped silently, so the .env editor says
    // where a connection string is actually meant to point.
    expect(screen.getByText(/Settings → Network/)).toBeTruthy();

    // The plaintext is only fetched once the user expands the editor.
    expect(envSpy).not.toHaveBeenCalled();
    const textarea = screen.getByPlaceholderText(
      /injected into preview processes/i,
    ) as HTMLTextAreaElement;
    await openEnvEditor();
    expect(envSpy).toHaveBeenCalledWith("repo-1");
    await waitFor(() => {
      expect(textarea.value).toBe("STORED=1");
    });

    // Editing and saving persists via the repository update endpoint (repo-wide),
    // not a per-work-item call.
    await act(async () => {
      fireEvent.change(textarea, { target: { value: "API_TOKEN=x" } });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /Save \.env/i }));
      await Promise.resolve();
    });

    expect(updateSpy).toHaveBeenCalledWith(
      "repo-1",
      expect.objectContaining({ previewEnv: "API_TOKEN=x" }),
    );
  });

  test("emptying the custom .env editor removes the stored value", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "stopped",
      worktreePath: "/wt",
      configPath: null,
      profileName: null,
      publicHost: null,
      stateDirectory: null,
      message: null,
      services: [],
    });
    const updateSpy = vi.spyOn(authServices.repositoryService, "update");
    vi.spyOn(authServices.repositoryService, "getPreviewEnv").mockResolvedValue("STORED=1");
    const clearSpy = vi
      .spyOn(authServices.repositoryService, "clearPreviewEnv")
      .mockResolvedValue({} as never);

    await renderPreview();

    const textarea = (await screen.findByPlaceholderText(
      /injected into preview processes/i,
    )) as HTMLTextAreaElement;
    await openEnvEditor();
    await waitFor(() => {
      expect(textarea.value).toBe("STORED=1");
    });

    await act(async () => {
      fireEvent.change(textarea, { target: { value: "" } });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /Remove \.env/i }));
      await Promise.resolve();
    });

    // The repository update endpoint reads a blank .env as "keep it", so removing
    // has to go through the dedicated clear call.
    expect(clearSpy).toHaveBeenCalledWith("repo-1");
    expect(updateSpy).not.toHaveBeenCalled();
  });

  test("editing a service port sends it as an override when starting the preview", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "stopped",
      worktreePath: "/wt",
      configPath: "/wt/ild.config.json",
      profileName: "app",
      publicHost: null,
      stateDirectory: null,
      message: null,
      services: [
        {
          name: "web",
          portAlias: "web",
          status: "stopped",
          port: null,
          suggestedPort: 5173,
          healthUrl: null,
          publicUrl: null,
          logFilePath: null,
          processId: null,
          exitCode: null,
        },
      ],
    });
    const startSpy = vi.spyOn(authServices.workItemService, "startPreview").mockResolvedValue({
      configured: true,
      state: "running",
      worktreePath: "/wt",
      configPath: "/wt/ild.config.json",
      profileName: "app",
      publicHost: "127.0.0.1",
      stateDirectory: "/state",
      message: null,
      services: [],
    });
    await renderPreview();

    const portInput = (await screen.findByLabelText("Port for web")) as HTMLInputElement;
    expect(portInput.value).toBe("5173");

    await act(async () => {
      fireEvent.change(portInput, { target: { value: "4000" } });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Start Preview" }));
      await Promise.resolve();
    });

    expect(startSpy).toHaveBeenCalledWith("wi-1", { portOverrides: { web: 4000 } });
  });

  test("the log toggle fetches and shows a service's captured output", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "failed",
      worktreePath: "/wt",
      configPath: "/wt/ild.config.json",
      profileName: "app",
      publicHost: "127.0.0.1",
      stateDirectory: "/state",
      message: null,
      services: [
        {
          name: "api",
          portAlias: "api",
          status: "exited",
          port: 8080,
          suggestedPort: 8080,
          healthUrl: "http://127.0.0.1:8080/health",
          publicUrl: null,
          logFilePath: "/state/api.log",
          processId: 43,
          exitCode: 1,
        },
      ],
    });
    const logSpy = vi
      .spyOn(authServices.workItemService, "getPreviewLogs")
      .mockResolvedValue({ service: "api", content: "Error: port already in use" });
    await renderPreview();

    await act(async () => {
      fireEvent.click(await screen.findByRole("button", { name: "Log" }));
      await Promise.resolve();
    });

    expect(logSpy).toHaveBeenCalledWith("wi-1", "api");
    expect(await screen.findByText("Error: port already in use")).toBeTruthy();
  });

  test("the per-row Start button starts only that service, passing its edited port", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "stopped",
      worktreePath: "/wt",
      configPath: "/wt/ild.config.json",
      profileName: "app",
      publicHost: null,
      stateDirectory: null,
      message: null,
      services: [
        {
          name: "web",
          portAlias: "web",
          status: "stopped",
          port: null,
          suggestedPort: 5173,
          healthUrl: null,
          publicUrl: null,
          logFilePath: null,
          processId: null,
          exitCode: null,
        },
        {
          name: "api",
          portAlias: "api",
          status: "stopped",
          port: null,
          suggestedPort: 8080,
          healthUrl: null,
          publicUrl: null,
          logFilePath: null,
          processId: null,
          exitCode: null,
        },
      ],
    });
    const startServiceSpy = vi
      .spyOn(authServices.workItemService, "startPreviewService")
      .mockResolvedValue({
        configured: true,
        state: "partial",
        worktreePath: "/wt",
        configPath: "/wt/ild.config.json",
        profileName: "app",
        publicHost: "127.0.0.1",
        stateDirectory: "/state",
        message: null,
        services: [],
      });
    await renderPreview();

    const portInput = (await screen.findByLabelText("Port for web")) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(portInput, { target: { value: "4000" } });
      await Promise.resolve();
    });

    // Two rows each have a "Start" button; the first belongs to the "web" row.
    const startButtons = screen.getAllByRole("button", { name: "Start" });
    await act(async () => {
      fireEvent.click(startButtons[0]);
      await Promise.resolve();
    });

    expect(startServiceSpy).toHaveBeenCalledWith("wi-1", "web", { portOverrides: { web: 4000 } });
  });

  test("the per-row Stop button stops only that service", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "running",
      worktreePath: "/wt",
      configPath: "/wt/ild.config.json",
      profileName: "app",
      publicHost: "127.0.0.1",
      stateDirectory: "/state",
      message: null,
      services: [
        {
          name: "web",
          portAlias: "web",
          status: "running",
          port: 5173,
          suggestedPort: 5173,
          healthUrl: "http://127.0.0.1:5173/",
          publicUrl: "http://127.0.0.1:5173",
          logFilePath: "/state/web.log",
          processId: 42,
          exitCode: null,
        },
      ],
    });
    const stopServiceSpy = vi
      .spyOn(authServices.workItemService, "stopPreviewService")
      .mockResolvedValue({
        configured: true,
        state: "stopped",
        worktreePath: "/wt",
        configPath: "/wt/ild.config.json",
        profileName: "app",
        publicHost: "127.0.0.1",
        stateDirectory: "/state",
        message: null,
        services: [],
      });
    await renderPreview();

    await act(async () => {
      fireEvent.click(await screen.findByRole("button", { name: "Stop" }));
      await Promise.resolve();
    });

    expect(stopServiceSpy).toHaveBeenCalledWith("wi-1", "web");
  });

  test("the config toggle loads a service's config and saving persists the edit", async () => {
    mockServices();
    vi.spyOn(authServices.workItemService, "getPreview").mockResolvedValue({
      configured: true,
      state: "stopped",
      worktreePath: "/wt",
      configPath: "/wt/ild.config.json",
      profileName: "app",
      publicHost: null,
      stateDirectory: null,
      message: null,
      services: [
        {
          name: "web",
          portAlias: "web",
          status: "stopped",
          port: null,
          suggestedPort: 5173,
          healthUrl: null,
          publicUrl: null,
          logFilePath: null,
          processId: null,
          exitCode: null,
        },
      ],
    });
    const configSpy = vi
      .spyOn(authServices.workItemService, "getPreviewServiceConfig")
      .mockResolvedValue({ service: "web", config: '{\n  "name": "web"\n}' });
    const saveSpy = vi
      .spyOn(authServices.workItemService, "updatePreviewServiceConfig")
      .mockResolvedValue({
        service: "web",
        config: '{\n  "name": "web",\n  "suggestedPort": 4000\n}',
      });
    await renderPreview();

    await act(async () => {
      fireEvent.click(await screen.findByRole("button", { name: "Config" }));
      await Promise.resolve();
    });

    expect(configSpy).toHaveBeenCalledWith("wi-1", "web");
    const editor = (await screen.findByLabelText("Config for web")) as HTMLTextAreaElement;
    expect(editor.value).toBe('{\n  "name": "web"\n}');

    const edited = '{\n  "name": "web",\n  "suggestedPort": 4000\n}';
    await act(async () => {
      fireEvent.change(editor, { target: { value: edited } });
      await Promise.resolve();
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: "Save" }));
      await Promise.resolve();
    });

    expect(saveSpy).toHaveBeenCalledWith("wi-1", "web", edited);
  });
});
