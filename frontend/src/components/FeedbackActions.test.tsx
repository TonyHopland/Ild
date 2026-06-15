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

  test("no Merge button when onMerge is not provided", () => {
    render(
      <FeedbackActions actions={null} onApprove={vi.fn()} onReject={vi.fn()} onEdge={vi.fn()} />,
    );
    expect(screen.queryByText("Merge")).toBeNull();
  });

  test("confirming Merge calls onMerge with delete-branch checked by default", () => {
    const onMerge = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
        onMerge={onMerge}
      />,
    );

    // The confirmation popup is only shown after clicking Merge.
    expect(screen.queryByText("Delete branch after merge")).toBeNull();
    fireEvent.click(screen.getByText("Merge"));

    const checkbox = screen.getByRole("checkbox") as HTMLInputElement;
    expect(checkbox.checked).toBe(true);

    fireEvent.click(screen.getByText("Confirm Merge"));
    expect(onMerge).toHaveBeenCalledWith(true);
  });

  test("unchecking the box merges without deleting the branch", () => {
    const onMerge = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
        onMerge={onMerge}
      />,
    );

    fireEvent.click(screen.getByText("Merge"));
    fireEvent.click(screen.getByRole("checkbox"));
    fireEvent.click(screen.getByText("Confirm Merge"));
    expect(onMerge).toHaveBeenCalledWith(false);
  });

  test("cancelling the Merge confirmation does not call onMerge", () => {
    const onMerge = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
        onMerge={onMerge}
      />,
    );

    fireEvent.click(screen.getByText("Merge"));
    fireEvent.click(screen.getByText("Cancel"));
    expect(onMerge).not.toHaveBeenCalled();
    expect(screen.queryByText("Delete branch after merge")).toBeNull();
  });
});
