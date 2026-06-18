import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import type { Node } from "@xyflow/react";
import { NodeType } from "../../../types";
import { NodeSettingsModal } from "./NodeSettingsModal";

function makeNode(type: NodeType): Node {
  return {
    id: "n1",
    position: { x: 0, y: 0 },
    data: { type, label: "Node" },
  };
}

function renderModal(overrides: Partial<Parameters<typeof NodeSettingsModal>[0]> = {}) {
  const props: Parameters<typeof NodeSettingsModal>[0] = {
    selectedNode: makeNode(NodeType.Start),
    labelError: null,
    nodeLabel: "Start",
    cmdCommand: "",
    aiPrompt: "",
    aiProvider: "",
    aiTools: [],
    aiMatchRules: [],
    customEdgeNames: [],
    aiUseSession: false,
    aiSessionPlaceholder: "",
    aiForkFromPlaceholder: "",
    startCreateWorktree: true,
    startRunInstall: false,
    humanInputLabel: "",
    humanPrompt: "",
    promptNodePrompt: "",
    prDescriptionTemplate: "",
    prCommentTemplate: "",
    aiProviders: [],
    availableAiTools: [],
    adapterConfigSchema: [],
    adapterConfigValues: {},
    sessionPlaceholderUsages: [],
    selectedPlaceholderUsage: undefined,
    onClose: vi.fn(),
    onDeleteNode: vi.fn(),
    onSave: vi.fn(),
    onValidateLabel: vi.fn(),
    onNodeLabelChange: vi.fn(),
    onCmdCommandChange: vi.fn(),
    onAiPromptChange: vi.fn(),
    onAiProviderChange: vi.fn(),
    onAiToolsChange: vi.fn(),
    onAiMatchRulesChange: vi.fn(),
    onCustomEdgeNamesChange: vi.fn(),
    onAiUseSessionChange: vi.fn(),
    onAiSessionPlaceholderChange: vi.fn(),
    onAiForkFromPlaceholderChange: vi.fn(),
    onStartCreateWorktreeChange: vi.fn(),
    onStartRunInstallChange: vi.fn(),
    onHumanInputLabelChange: vi.fn(),
    onHumanPromptChange: vi.fn(),
    onPromptNodePromptChange: vi.fn(),
    onPrDescriptionTemplateChange: vi.fn(),
    onPrCommentTemplateChange: vi.fn(),
    onAdapterConfigChange: vi.fn(),
    ...overrides,
  };
  render(<NodeSettingsModal {...props} />);
  return props;
}

describe("NodeSettingsModal install-on-start option", () => {
  afterEach(() => {
    cleanup();
  });

  test("renders the install checkbox unchecked for a Start node", () => {
    renderModal({ startRunInstall: false });

    const checkbox = screen.getByLabelText("Run ild.config install") as HTMLInputElement;
    expect(checkbox.type).toBe("checkbox");
    expect(checkbox.checked).toBe(false);
  });

  test("reflects an enabled install option as checked", () => {
    renderModal({ startRunInstall: true });

    const checkbox = screen.getByLabelText("Run ild.config install") as HTMLInputElement;
    expect(checkbox.checked).toBe(true);
  });

  test("notifies when the install option is toggled on", () => {
    const props = renderModal({ startRunInstall: false });

    fireEvent.click(screen.getByLabelText("Run ild.config install"));

    expect(props.onStartRunInstallChange).toHaveBeenCalledWith(true);
  });

  test("does not show the install option for non-Start nodes", () => {
    renderModal({ selectedNode: makeNode(NodeType.Cmd) });

    expect(screen.queryByLabelText("Run ild.config install")).toBeNull();
  });
});

describe("NodeSettingsModal session memory mode", () => {
  afterEach(() => {
    cleanup();
  });

  test("defaults to None for an AI node without a session", () => {
    renderModal({ selectedNode: makeNode(NodeType.AI), aiUseSession: false });

    expect(screen.getByRole("radio", { name: "None" }).getAttribute("aria-checked")).toBe("true");
    expect(screen.queryByLabelText("Session name")).toBeNull();
    expect(screen.queryByLabelText("Fork from")).toBeNull();
  });

  test("enables a session when Continue is selected", () => {
    const props = renderModal({ selectedNode: makeNode(NodeType.AI), aiUseSession: false });

    fireEvent.click(screen.getByRole("radio", { name: "Continue a session" }));

    expect(props.onAiUseSessionChange).toHaveBeenCalledWith(true);
    expect(props.onAiForkFromPlaceholderChange).toHaveBeenCalledWith("");
  });

  test("shows the session name field when continuing a session", () => {
    renderModal({
      selectedNode: makeNode(NodeType.AI),
      aiUseSession: true,
      aiSessionPlaceholder: "research",
    });

    const input = screen.getByLabelText("Session name") as HTMLInputElement;
    expect(input.value).toBe("research");
  });

  test("renders the source picker and destination when forking", () => {
    renderModal({
      selectedNode: makeNode(NodeType.AI),
      aiUseSession: true,
      aiForkFromPlaceholder: "base",
      aiSessionPlaceholder: "variant-a",
      sessionPlaceholderUsages: [{ name: "base", count: 1 }],
    });

    expect((screen.getByLabelText("Fork from") as HTMLSelectElement).value).toBe("base");
    expect((screen.getByLabelText("Into new session") as HTMLInputElement).value).toBe("variant-a");
  });

  test("notifies when the fork source is chosen", () => {
    const props = renderModal({
      selectedNode: makeNode(NodeType.AI),
      aiUseSession: true,
      aiForkFromPlaceholder: "base",
      sessionPlaceholderUsages: [
        { name: "base", count: 1 },
        { name: "other", count: 2 },
      ],
    });

    fireEvent.change(screen.getByLabelText("Fork from"), { target: { value: "other" } });

    expect(props.onAiForkFromPlaceholderChange).toHaveBeenCalledWith("other");
  });
});
