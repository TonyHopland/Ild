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

  const hiding =
    (...hidden: string[]) =>
    (name: string) =>
      !hidden.includes(name);

  const buttonNames = () => screen.queryAllByRole("button").map((b) => b.textContent);

  test.each([
    ["OnSuccess,Respond,Escalate,OnFailure", ["OnSuccess"], ["Respond", "Escalate", "Reject"]],
    ["OnSuccess,Respond,Escalate,OnFailure", ["OnFailure"], ["Approve", "Respond", "Escalate"]],
    ["OnSuccess,Respond,Escalate,OnFailure", ["Escalate"], ["Approve", "Respond", "Reject"]],
    [
      "OnSuccess,constructor,toString,OnFailure",
      ["toString"],
      ["Approve", "constructor", "Reject"],
    ],
    [null, ["OnSuccess"], ["Reject"]],
    [null, ["OnFailure"], ["Approve"]],
    ["", ["OnFailure"], ["Approve"]],
  ])("actions %j with %j hidden offers only the rest", (actions, hidden, expected) => {
    render(
      <FeedbackActions
        actions={actions}
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
        isVisible={hiding(...hidden)}
      />,
    );

    expect(buttonNames()).toHaveLength(expected.length);
    expect(new Set(buttonNames())).toEqual(new Set(expected));
  });

  test.each([["OnSuccess,Respond,OnFailure"], [null]])(
    "with every output of %j hidden there is no output button, and no fallback to Approve and Reject",
    (actions) => {
      render(
        <FeedbackActions
          actions={actions}
          onApprove={vi.fn()}
          onReject={vi.fn()}
          onEdge={vi.fn()}
          isVisible={() => false}
        />,
      );

      expect(buttonNames()).toEqual([]);
    },
  );

  test("a visible output beside hidden ones still submits as before", () => {
    const onEdge = vi.fn();
    const onReject = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,Respond,Escalate,OnFailure"
        onApprove={vi.fn()}
        onReject={onReject}
        onEdge={onEdge}
        isVisible={hiding("OnSuccess", "Escalate")}
      />,
    );

    expect(screen.queryByText("Approve")).toBeNull();
    expect(screen.queryByText("Escalate")).toBeNull();
    fireEvent.click(screen.getByText("Respond"));
    fireEvent.click(screen.getByText("Reject"));
    expect(onEdge).toHaveBeenCalledWith("Respond");
    expect(onReject).toHaveBeenCalledTimes(1);
  });

  test("Merge and its confirmation work with every output hidden", () => {
    const onMerge = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,OnFailure,on_merged"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
        onMerge={onMerge}
        isVisible={() => false}
      />,
    );

    expect(buttonNames()).toEqual(["Merge"]);
    fireEvent.click(screen.getByText("Merge"));
    expect((screen.getByRole("checkbox") as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByText("Confirm Merge"));
    expect(onMerge).toHaveBeenCalledWith(true);
  });
});
