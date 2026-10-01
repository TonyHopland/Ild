import { describe, expect, test, vi, afterEach } from "vite-plus/test";
import { render, screen, fireEvent, cleanup, within } from "@testing-library/react";
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

  test.each([
    ["Approve", "OnSuccess", "onApprove"],
    ["Reject", "OnFailure", "onReject"],
    ["Escalate", "Escalate", "onEdge"],
  ] as const)(
    "%s on an output that asks to confirm sends nothing until confirmed, in the button's colour",
    (label, output, handler) => {
      const handlers = { onApprove: vi.fn(), onReject: vi.fn(), onEdge: vi.fn() };
      render(
        <FeedbackActions
          actions="OnSuccess,Escalate,OnFailure"
          {...handlers}
          needsConfirm={(name) => name === output}
        />,
      );

      const pressed = screen.getByRole("button", { name: label });
      const tone = [...pressed.classList].find((name) =>
        /^btn-(primary|warning|danger)$/.test(name),
      );
      fireEvent.click(pressed);
      const dialog = screen.getByRole("dialog", { name: `Confirm ${label}` });
      expect(dialog.textContent).toContain(`"${label}"`);
      expect(within(dialog).getByRole("button", { name: label }).className).toBe(`btn ${tone}`);
      for (const called of Object.values(handlers)) expect(called).not.toHaveBeenCalled();

      fireEvent.click(within(dialog).getByRole("button", { name: label }));
      expect(screen.queryByRole("dialog")).toBeNull();
      expect(handlers[handler]).toHaveBeenCalledTimes(1);
      if (handler === "onEdge") expect(handlers.onEdge).toHaveBeenCalledWith("Escalate");
      for (const [name, called] of Object.entries(handlers)) {
        if (name !== handler) expect(called).not.toHaveBeenCalled();
      }
    },
  );

  test("cancelling the confirmation sends nothing", () => {
    const onEdge = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,Escalate,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={onEdge}
        needsConfirm={(name) => name === "Escalate"}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Escalate" }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onEdge).not.toHaveBeenCalled();
  });

  test("outputs that do not ask to confirm send at once", () => {
    const onApprove = vi.fn();
    const onEdge = vi.fn();
    render(
      <FeedbackActions
        actions="OnSuccess,Escalate,OnFailure"
        onApprove={onApprove}
        onReject={vi.fn()}
        onEdge={onEdge}
        needsConfirm={(name) => name === "OnFailure"}
      />,
    );

    fireEvent.click(screen.getByText("Approve"));
    fireEvent.click(screen.getByText("Escalate"));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onApprove).toHaveBeenCalledTimes(1);
    expect(onEdge).toHaveBeenCalledWith("Escalate");
  });

  test("a confirmation still open when the parked node is read anew is dropped unsent", () => {
    const onEdge = vi.fn();
    const asks = (name: string) => name === "Escalate";
    const actions = (needsConfirm: (name: string) => boolean) => (
      <FeedbackActions
        actions="OnSuccess,Escalate,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={onEdge}
        needsConfirm={needsConfirm}
      />
    );
    const { rerender } = render(actions(asks));

    fireEvent.click(screen.getByRole("button", { name: "Escalate" }));
    rerender(actions(asks));
    expect(screen.getByRole("dialog", { name: "Confirm Escalate" })).toBeTruthy();

    rerender(actions((name) => name === "Escalate"));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(onEdge).not.toHaveBeenCalled();
  });
});

/** `color` as the browser stores it in an inline style, so values can be compared. */
function cssColor(color: string) {
  const probe = document.createElement("span");
  probe.style.color = color;
  return probe.style.color;
}

const BLACK = new Set([cssColor("#000"), cssColor("black")]);
const WHITE = new Set([cssColor("#fff"), cssColor("white")]);

const PURPLE = "#7e22ce";
const YELLOW = "#fde047";

function expectColoured(button: HTMLElement, color: string, text: Set<string>) {
  expect(button.style.backgroundColor).toBe(cssColor(color));
  expect(text.has(button.style.color), button.style.color).toBe(true);
}

function expectUncoloured(button: HTMLElement) {
  expect(button.style.backgroundColor).toBe("");
  expect(button.style.color).toBe("");
}

describe("FeedbackActions — button colours", () => {
  test.each([
    ["Approve", "OnSuccess", "btn-primary", "onApprove"],
    ["Reject", "OnFailure", "btn-danger", "onReject"],
    ["Escalate", "Escalate", "btn-warning", "onEdge"],
  ] as const)(
    "%s takes its output's colour with readable text, keeps its class, and still sends the same output",
    (label, output, tone, handler) => {
      const handlers = { onApprove: vi.fn(), onReject: vi.fn(), onEdge: vi.fn() };
      render(
        <FeedbackActions
          actions="OnSuccess,Escalate,Respond,OnFailure"
          {...handlers}
          colorOf={(name) => (name === output ? PURPLE : name === "Respond" ? YELLOW : null)}
        />,
      );

      const pressed = screen.getByRole("button", { name: label });
      expectColoured(pressed, PURPLE, WHITE);
      expect(pressed.classList.contains("btn")).toBe(true);
      expect(pressed.classList.contains(tone)).toBe(true);
      expectColoured(screen.getByRole("button", { name: "Respond" }), YELLOW, BLACK);
      for (const other of ["Approve", "Escalate", "Reject"].filter((name) => name !== label)) {
        expectUncoloured(screen.getByRole("button", { name: other }));
      }

      fireEvent.click(pressed);
      expect(handlers[handler]).toHaveBeenCalledTimes(1);
      if (handler === "onEdge") expect(handlers.onEdge).toHaveBeenCalledWith("Escalate");
    },
  );

  test("with no colour every button renders as it always has", () => {
    render(
      <FeedbackActions
        actions="OnSuccess,Escalate,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
        onMerge={vi.fn()}
        colorOf={() => null}
      />,
    );

    for (const button of screen.getAllByRole("button")) {
      expect(button.getAttribute("style"), button.textContent ?? "").toBeNull();
    }
    expect(screen.getByRole("button", { name: "Approve" }).className).toBe(
      "btn btn-sm btn-primary",
    );
    expect(screen.getByRole("button", { name: "Escalate" }).className).toBe(
      "btn btn-sm btn-warning",
    );
    expect(screen.getByRole("button", { name: "Reject" }).className).toBe("btn btn-sm btn-danger");
  });

  test.each([
    ["Approve", "OnSuccess", "btn-primary", YELLOW, BLACK],
    ["Escalate", "Escalate", "btn-warning", PURPLE, WHITE],
    ["Reject", "OnFailure", "btn-danger", PURPLE, WHITE],
  ] as const)(
    "confirming %s offers a confirm button in the pressed button's colour",
    (label, output, tone, color, text) => {
      const handlers = { onApprove: vi.fn(), onReject: vi.fn(), onEdge: vi.fn() };
      render(
        <FeedbackActions
          actions="OnSuccess,Escalate,OnFailure"
          {...handlers}
          needsConfirm={(name) => name === output}
          colorOf={(name) => (name === output ? color : null)}
        />,
      );

      fireEvent.click(screen.getByRole("button", { name: label }));
      const dialog = screen.getByRole("dialog", { name: `Confirm ${label}` });
      const confirm = within(dialog).getByRole("button", { name: label });
      expect(confirm.className).toBe(`btn ${tone}`);
      expectColoured(confirm, color, text);
      expectUncoloured(within(dialog).getByRole("button", { name: "Cancel" }));

      fireEvent.click(confirm);
      const sent = { OnSuccess: "onApprove", OnFailure: "onReject", Escalate: "onEdge" } as const;
      expect(handlers[sent[output]]).toHaveBeenCalledTimes(1);
    },
  );

  test("confirming an uncoloured output offers the confirm button with no inline colour", () => {
    render(
      <FeedbackActions
        actions="OnSuccess,Escalate,OnFailure"
        onApprove={vi.fn()}
        onReject={vi.fn()}
        onEdge={vi.fn()}
        needsConfirm={(name) => name === "Escalate"}
        colorOf={(name) => (name === "OnSuccess" ? PURPLE : null)}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "Escalate" }));
    const dialog = screen.getByRole("dialog", { name: "Confirm Escalate" });
    expect(
      within(dialog).getByRole("button", { name: "Escalate" }).getAttribute("style"),
    ).toBeNull();
  });
});
