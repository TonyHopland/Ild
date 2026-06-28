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

  test("routes its branches through the shared custom outlet, with no success outlet", () => {
    renderNode(NodeType.Condition);

    // A Condition node's "true"/"false" branches leave the single custom
    // ("respond") outlet, like a PR node's on_* edges. The fail handle stays
    // for an evaluation error, but there is no default success outlet.
    expect(screen.getByTestId("source-handle-respond")).toBeTruthy();
    expect(screen.getByTestId("source-handle-fail")).toBeTruthy();
    expect(screen.queryByTestId("source-handle-success")).toBeNull();
    // No bespoke true/false handles.
    expect(screen.queryByTestId("source-handle-true")).toBeNull();
    expect(screen.queryByTestId("source-handle-false")).toBeNull();
  });

  test("a non-condition node keeps the success and fail outlets", () => {
    renderNode(NodeType.Cmd);

    expect(screen.getByTestId("source-handle-success")).toBeTruthy();
    expect(screen.getByTestId("source-handle-fail")).toBeTruthy();
  });
});
