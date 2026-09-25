import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, waitFor, cleanup, fireEvent, within, act } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router";
import { AuthContext } from "../../hooks/useAuth";
import { EdgeType, NodeType, RecoveryPolicy } from "../../types";
import type { AiProvider } from "../../types";

// An AI node picks its provider by tag: the provider holding the tag
// (case-insensitively, after trimming), else the default provider, else none.
// The editor must show and act on exactly that rule.

const { loopTemplateService, aiProviderService } = vi.hoisted(() => ({
  loopTemplateService: {
    getAll: vi.fn(),
    validate: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
  },
  aiProviderService: { getAll: vi.fn() },
}));

vi.mock("../../hooks/useSignalR", () => ({
  useSignalR: () => ({
    connectionState: "connected",
    on: vi.fn(),
    off: vi.fn(),
    invoke: vi.fn(() => Promise.resolve()),
  }),
}));

vi.mock("../../services/auth", () => ({
  loopTemplateService,
  aiProviderService,
}));

import LoopEditor from "./index";

const alpha: AiProvider = {
  id: "p-alpha",
  name: "Alpha",
  type: "pi",
  baseUrl: "https://alpha.local",
  apiKey: "",
  model: "m",
  isDefault: true,
  parallelism: 0,
  tags: ["Fast"],
  supportedTools: [
    { key: "read", label: "Read files", description: "", defaultEnabled: true },
    { key: "write", label: "Write files", description: "", defaultEnabled: true },
    { key: "execute", label: "Run commands", description: "", defaultEnabled: false },
  ],
  createdAt: "2025-01-01T00:00:00Z",
};

const beta: AiProvider = {
  id: "p-beta",
  name: "Beta",
  type: "claude-code",
  baseUrl: "",
  apiKey: "",
  model: "",
  isDefault: false,
  parallelism: 0,
  tags: ["QA", "Thinking"],
  supportedTools: [
    { key: "read", label: "Read files", description: "", defaultEnabled: true },
    { key: "ild", label: "ILD tools", description: "", defaultEnabled: true },
  ],
  createdAt: "2025-01-02T00:00:00Z",
};

function templateWithAiNode(aiConfig: Record<string, unknown>) {
  return {
    id: "tpl-1",
    name: "Dev Loop",
    description: "",
    version: 1,
    recoveryPolicy: RecoveryPolicy.AutoResume,
    nodes: [
      { id: "n-start", type: NodeType.Start, label: "Initialize", config: {} },
      { id: "n-ai", type: NodeType.AI, label: "Reviewer", config: aiConfig },
      { id: "n-cleanup", type: NodeType.Cleanup, label: "Tidy Up", config: {} },
    ],
    edges: [
      { id: "e-1", sourceNodeId: "n-start", targetNodeId: "n-ai", edgeType: EdgeType.OnSuccess },
      { id: "e-2", sourceNodeId: "n-ai", targetNodeId: "n-cleanup", edgeType: EdgeType.OnSuccess },
    ],
    createdAt: "2025-01-01T00:00:00Z",
    updatedAt: "2025-01-01T00:00:00Z",
    isArchived: false,
  };
}

/** A node on the default provider (Alpha) with a non-default allowlist. */
const customisedOnDefault = {
  prompt: "Review it",
  toolAllowlist: ["execute"],
};

const authValue = {
  user: { id: "1", username: "test", createdAt: "" },
  token: "test-token",
  isAuthenticated: true,
  isLoading: false,
  login: vi.fn(),
  logout: vi.fn(),
};

async function openAiNode(aiConfig: Record<string, unknown>, providers: AiProvider[]) {
  loopTemplateService.getAll.mockResolvedValue([templateWithAiNode(aiConfig)]);
  loopTemplateService.validate.mockResolvedValue({ valid: true, errors: [] });
  loopTemplateService.update.mockResolvedValue({ id: "tpl-1" });
  aiProviderService.getAll.mockResolvedValue(providers);

  render(
    <MemoryRouter initialEntries={["/loop-editor/tpl-1"]}>
      <AuthContext.Provider value={authValue}>
        <Routes>
          <Route path="/loop-editor" element={<LoopEditor />} />
          <Route path="/loop-editor/:templateId" element={<LoopEditor />} />
        </Routes>
      </AuthContext.Provider>
    </MemoryRouter>,
  );

  await waitFor(() => expect(screen.getByText("Reviewer")).toBeTruthy());
  // Providers load independently of the template; wait so the node opens against them.
  await waitFor(() => expect(aiProviderService.getAll).toHaveBeenCalled());
  await act(async () => {});
  fireEvent.click(screen.getByText("Reviewer"));
  const dialog = await screen.findByRole("dialog", { name: "Node Settings" });
  return dialog;
}

function tagField(dialog: HTMLElement) {
  return within(dialog).getByLabelText("Provider tag") as HTMLInputElement;
}

/** The element stating `statement`, whatever markup splits its text. */
function statement(dialog: HTMLElement, text: string | RegExp) {
  const matches = (value: string | null | undefined) =>
    value != null && (typeof text === "string" ? value.trim() === text : text.test(value));
  return within(dialog).queryAllByText(
    (_, el) =>
      matches(el?.textContent) && ![...(el?.children ?? [])].some((c) => matches(c.textContent)),
  )[0];
}

function toolIsChecked(dialog: HTMLElement, label: string) {
  return (within(dialog).getByLabelText(label) as HTMLInputElement).checked;
}

/** Saves the node, then the loop, and returns the AI node's config as persisted. */
async function saveAndReadAiConfig(dialog: HTMLElement) {
  fireEvent.click(within(dialog).getByText("Save"));
  await waitFor(() => expect(screen.queryByRole("dialog", { name: "Node Settings" })).toBeNull());
  fireEvent.click(screen.getByText("Save"));
  fireEvent.click(await screen.findByText("Save changes"));
  await waitFor(() => expect(loopTemplateService.update).toHaveBeenCalled());
  const calls = loopTemplateService.update.mock.calls;
  const payload = calls[calls.length - 1][1] as {
    nodes: Array<{ id: string; config: Record<string, unknown> }>;
  };
  return payload.nodes.find((n) => n.id === "n-ai")!.config;
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("Loop Editor — AI node provider tag", () => {
  test("replaces the provider selector with a tag field suggesting every existing tag", async () => {
    const dialog = await openAiNode({ prompt: "p" }, [alpha, beta]);

    expect(within(dialog).queryByLabelText("AI Provider")).toBeNull();
    const field = tagField(dialog);
    expect(field.value).toBe("");

    const listId = field.getAttribute("list");
    expect(listId).toBeTruthy();
    const suggestions = [...document.getElementById(listId!)!.querySelectorAll("option")].map(
      (option) => option.value,
    );
    expect([...suggestions].sort()).toEqual(["Fast", "QA", "Thinking"]);
  });

  test("a node with only a legacy provider id opens with an empty tag and saves without the id", async () => {
    const dialog = await openAiNode({ prompt: "p", aiProviderId: beta.id }, [alpha, beta]);

    expect(tagField(dialog).value).toBe("");
    expect(statement(dialog, "Runs on the default provider (Alpha)")).toBeTruthy();

    const config = await saveAndReadAiConfig(dialog);
    expect(config.aiProviderId).toBeUndefined();
    expect(config.aiProviderTag ?? "").toBe("");
  });

  test("saves the trimmed tag", async () => {
    const dialog = await openAiNode({ prompt: "p" }, [alpha, beta]);

    fireEvent.change(tagField(dialog), { target: { value: "  qa  " } });

    const config = await saveAndReadAiConfig(dialog);
    expect(config.aiProviderTag).toBe("qa");
    expect(config.aiProviderId).toBeUndefined();
  });

  test.each([
    ["", "Runs on the default provider (Alpha)"],
    ["  ", "Runs on the default provider (Alpha)"],
    ["thinking", "Runs on Beta"],
    [" QA ", "Runs on Beta"],
    ["fast", "Runs on Alpha"],
    ["Nightly", "No provider has this tag — runs on the default provider (Alpha)"],
  ])("tag %j states: %s", async (tag, expected) => {
    const dialog = await openAiNode({ prompt: "p" }, [alpha, beta]);

    fireEvent.change(tagField(dialog), { target: { value: tag } });

    expect(statement(dialog, expected)).toBeTruthy();
  });

  test("a tag nobody holds with no default provider says so and offers no tools", async () => {
    const noDefault = { ...alpha, isDefault: false };
    const dialog = await openAiNode({ prompt: "p", aiProviderTag: "Nightly" }, [noDefault, beta]);

    expect(statement(dialog, /no default provider/i)).toBeTruthy();
    expect(within(dialog).queryByLabelText("Read files")).toBeNull();

    // A tag that resolves brings its provider's tools back.
    fireEvent.change(tagField(dialog), { target: { value: "QA" } });
    expect(statement(dialog, "Runs on Beta")).toBeTruthy();
    expect(toolIsChecked(dialog, "ILD tools")).toBe(true);
  });

  test("the tools are those of the tag's provider when the node opens", async () => {
    const dialog = await openAiNode({ prompt: "p", aiProviderTag: "qa" }, [alpha, beta]);

    expect(statement(dialog, "Runs on Beta")).toBeTruthy();
    expect(toolIsChecked(dialog, "ILD tools")).toBe(true);
    expect(within(dialog).queryByLabelText("Run commands")).toBeNull();
  });

  test("typing a tag that still resolves to the same provider keeps the allowlist", async () => {
    const dialog = await openAiNode(customisedOnDefault, [alpha, beta]);
    expect(toolIsChecked(dialog, "Run commands")).toBe(true);
    expect(toolIsChecked(dialog, "Read files")).toBe(false);

    // Every step resolves to Alpha: unknown prefixes fall back to the default
    // (Alpha), "Fast" is Alpha's own tag, and "FAST" only changes the case.
    for (const value of ["N", "No", "Nope", "", "F", "Fa", "Fas", "Fast", "FAST"]) {
      fireEvent.change(tagField(dialog), { target: { value } });
    }
    await Promise.resolve();

    expect(toolIsChecked(dialog, "Run commands")).toBe(true);
    expect(toolIsChecked(dialog, "Read files")).toBe(false);

    const config = await saveAndReadAiConfig(dialog);
    expect(config.aiProviderTag).toBe("FAST");
    expect(config.toolAllowlist).toEqual(["execute"]);
  });

  test("a tag that resolves to a different provider resets to that provider's defaults", async () => {
    const dialog = await openAiNode(customisedOnDefault, [alpha, beta]);

    // "Q" still falls back to Alpha; "QA" is Beta's.
    fireEvent.change(tagField(dialog), { target: { value: "Q" } });
    expect(toolIsChecked(dialog, "Run commands")).toBe(true);
    fireEvent.change(tagField(dialog), { target: { value: "QA" } });

    expect(toolIsChecked(dialog, "Read files")).toBe(true);
    expect(toolIsChecked(dialog, "ILD tools")).toBe(true);

    const config = await saveAndReadAiConfig(dialog);
    expect(config.aiProviderTag).toBe("QA");
    expect([...(config.toolAllowlist as string[])].sort()).toEqual(["ild", "read"]);
  });

  test("saving drops the node's own adapter settings, which nothing reads", async () => {
    const dialog = await openAiNode(
      { ...customisedOnDefault, adapterConfig: { reasoning: "high" } },
      [alpha, beta],
    );

    const config = await saveAndReadAiConfig(dialog);
    expect(JSON.parse(JSON.stringify(config))).not.toHaveProperty("adapterConfig");
    expect(config.toolAllowlist).toEqual(["execute"]);
  });
});
