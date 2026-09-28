import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import Repositories from "./index";
import * as services from "../../services/auth";

const provider = {
  id: "prov-1",
  name: "Forgejo",
  type: "gitea",
  baseUrl: "https://git.example.com",
  apiKey: "",
  webhookSecret: "",
  createdAt: "2025-01-01T00:00:00Z",
};

const feeds = [
  {
    id: "f1",
    name: "company",
    feedUrl: "https://pkgs.dev.azure.com/example-org/_packaging/company",
    patHint: "••••3fa9",
    createdAt: "2026-09-01T10:00:00Z",
    updatedAt: "2026-09-01T10:00:00Z",
  },
  {
    id: "f2",
    name: "tools",
    feedUrl: "https://pkgs.dev.azure.com/example-org/example-project/_packaging/tools",
    patHint: "••••",
    createdAt: "2026-09-02T10:00:00Z",
    updatedAt: "2026-09-02T10:00:00Z",
  },
];

function repo(packageFeeds?: { name: string; missing: boolean }[]) {
  return {
    id: "repo-1",
    name: "my-repo",
    cloneUrl: "https://git.example.com/my-repo.git",
    remoteProviderId: "prov-1",
    defaultBranch: "main",
    worktreesPath: null,
    defaultIntakeStatus: "Backlog",
    hasPreviewEnv: false,
    createdAt: "2025-01-01T00:00:00Z",
    ...(packageFeeds ? { packageFeeds } : {}),
  };
}

function mockServices(repos: unknown[]) {
  vi.spyOn(services.repositoryService, "getAll").mockResolvedValue(repos as any);
  vi.spyOn(services.remoteProviderService, "getAll").mockResolvedValue([provider] as any);
  vi.spyOn(services.packageFeedService, "list").mockResolvedValue(feeds as any);
  vi.spyOn(services.repositoryService, "getPreviewEnv").mockResolvedValue("");
  vi.spyOn(services.repositoryService, "inspectRemote").mockResolvedValue({} as any);
  return {
    update: vi.spyOn(services.repositoryService, "update").mockResolvedValue(repo() as any),
    create: vi.spyOn(services.repositoryService, "create").mockResolvedValue(repo() as any),
  };
}

const feedBox = (name: RegExp) => screen.getByRole("checkbox", { name }) as HTMLInputElement;

function sentFeeds(spy: { mock: { calls: unknown[][] } }, argIndex: number) {
  const payload = spy.mock.calls[0][argIndex] as { packageFeeds?: string[] };
  return [...(payload.packageFeeds ?? ["<absent>"])].sort();
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("Repositories page package feeds", () => {
  test("edit shows the selection, marks a deleted feed missing, and saves the changed selection", async () => {
    const { update } = mockServices([
      repo([
        { name: "company", missing: false },
        { name: "gone", missing: true },
      ]),
    ]);
    render(
      <MemoryRouter>
        <Repositories />
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));

    await waitFor(() => expect(feedBox(/^company/).checked).toBe(true));
    expect(feedBox(/^tools/).checked).toBe(false);
    const gone = feedBox(/^gone \(missing\)/);
    expect(gone.checked).toBe(true);

    fireEvent.click(gone);
    fireEvent.click(feedBox(/^tools/));
    expect(gone.checked).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Update" }));

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect(update.mock.calls[0][0]).toBe("repo-1");
    expect(sentFeeds(update, 1)).toEqual(["company", "tools"]);
  });

  test("a repository that selects nothing starts with every feed unchecked and saves an empty selection", async () => {
    const { update } = mockServices([repo()]);
    render(
      <MemoryRouter>
        <Repositories />
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));

    await waitFor(() => expect(feedBox(/^company/).checked).toBe(false));
    expect(feedBox(/^tools/).checked).toBe(false);
    fireEvent.click(screen.getByRole("button", { name: "Update" }));

    await waitFor(() => expect(update).toHaveBeenCalledTimes(1));
    expect(sentFeeds(update, 1)).toEqual([]);
  });

  test("a new repository saves the feeds checked on the create form", async () => {
    const { create } = mockServices([]);
    render(
      <MemoryRouter>
        <Repositories />
      </MemoryRouter>,
    );

    fireEvent.click(await screen.findByRole("button", { name: /New Repository/ }));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "new-repo" } });
    fireEvent.change(screen.getByLabelText("Clone URL"), {
      target: { value: "https://git.example.com/new-repo.git" },
    });
    await waitFor(() => expect(feedBox(/^company/).checked).toBe(false));
    fireEvent.click(feedBox(/^company/));
    fireEvent.click(screen.getByRole("button", { name: "Create" }));

    await waitFor(() => expect(create).toHaveBeenCalledTimes(1));
    expect(sentFeeds(create, 0)).toEqual(["company"]);
  });
});
