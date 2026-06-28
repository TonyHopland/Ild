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

  test("reuses the success/fail outlets for its two branches", () => {
    renderNode(NodeType.Condition);

    // A Condition node routes its "true"/"false" branches through the standard
    // success/fail handles rather than inventing dedicated outlets.
    expect(screen.getByTestId("source-handle-success")).toBeTruthy();
    expect(screen.getByTestId("source-handle-fail")).toBeTruthy();
    // No bespoke true/false handles and no custom (respond) outlet.
    expect(screen.queryByTestId("source-handle-true")).toBeNull();
    expect(screen.queryByTestId("source-handle-false")).toBeNull();
    expect(screen.queryByTestId("source-handle-respond")).toBeNull();
  });

  test("a non-condition node keeps the success and fail outlets", () => {
    renderNode(NodeType.Cmd);

    expect(screen.getByTestId("source-handle-success")).toBeTruthy();
    expect(screen.getByTestId("source-handle-fail")).toBeTruthy();
  });
});
