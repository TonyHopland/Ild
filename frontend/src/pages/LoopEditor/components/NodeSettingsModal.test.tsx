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
