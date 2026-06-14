import { describe, expect, test, vi, afterEach } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import FeedbackActions from "./FeedbackActions";

afterEach(cleanup);

describe("FeedbackActions", () => {
  test("renders one button per connected custom edge name", () => {
    render(
      <FeedbackActions
        actions="OnSuccess,Respond,Escalate,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
      />,
    );

    expect(screen.getByText("Approve")).toBeTruthy();
    expect(screen.getByText("Reject")).toBeTruthy();
    expect(screen.getByText("Respond")).toBeTruthy();
    expect(screen.getByText("Escalate")).toBeTruthy();
  });

  test("clicking a custom edge button calls onEdge with that edge name", () => {
    const onEdge = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,Escalate,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={onEdge}
      />,
    );

    fireEvent.click(screen.getByText("Escalate"));
    expect(onEdge).toHaveBeenCalledWith("Escalate");
  });

  test("defaults to Approve + Reject with no custom buttons when actions are empty", () => {
    render(
      <FeedbackActions actions={null} onApprove={vi.fn()} onReject={vi.fn()} onEdge={vi.fn()} />,
    );

    expect(screen.getByText("Approve")).toBeTruthy();
    expect(screen.getByText("Reject")).toBeTruthy();
    // No custom edge tokens => only the two role buttons render.
    expect(screen.getAllByRole("button")).toHaveLength(2);
  });
});
