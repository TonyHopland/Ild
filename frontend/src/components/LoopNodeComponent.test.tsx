import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, screen, cleanup } from "@testing-library/react";
import { ReactFlowProvider, type NodeProps } from "@xyflow/react";
import { NodeType } from "../types";
import LoopNodeComponent from "./LoopNodeComponent";

// Handle reads from the React Flow store, so every node must render inside a
// provider — mirrors how the canvas mounts these components.
function renderNode(type: NodeType) {
  const props = {
    data: { type, label: type },
  } as unknown as NodeProps;
  render(
    <ReactFlowProvider>
      <LoopNodeComponent {...props} />
    </ReactFlowProvider>,
  );
}

describe("LoopNodeComponent condition handles", () => {
  afterEach(() => {
    cleanup();
  });

  test("renders exactly the two fixed true/false outlets", () => {
    renderNode(NodeType.Condition);

    expect(screen.getByTestId("source-handle-true")).toBeTruthy();
    expect(screen.getByTestId("source-handle-false")).toBeTruthy();
    // No free-form success or custom (respond) outlet on a Condition node.
    expect(screen.queryByTestId("source-handle-success")).toBeNull();
    expect(screen.queryByTestId("source-handle-respond")).toBeNull();
    // Still routes evaluation errors via the failure outlet.
    expect(screen.getByTestId("source-handle-fail")).toBeTruthy();
  });

  test("a non-condition node keeps the success outlet and has no true/false outlets", () => {
    renderNode(NodeType.Cmd);

    expect(screen.getByTestId("source-handle-success")).toBeTruthy();
    expect(screen.queryByTestId("source-handle-true")).toBeNull();
    expect(screen.queryByTestId("source-handle-false")).toBeNull();
  });
});
