import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, act } from "@testing-library/react";
import { useEffect, useRef, useState } from "react";
import { pressEscapeUntil } from "./test-support";

// These fixtures stand in for the WorkItem detail dialog and its Discard
// confirm. They reproduce the two properties that make Escape hard to press
// deterministically, without dragging the real components (and their services)
// into a test about the press itself:
//
//  - a press can be swallowed. A dialog's Escape listener is attached from a
//    passive effect, so it can be missing while the dialog is already in the
//    DOM. `ignoreFirst` models that window: a keydown that lands in it is gone
//    for good, because a keydown is never redelivered.
//  - a second press can undo the first. The confirm listens on the same
//    document and cancels on Escape, and it is registered after the dialog's
//    listener, so once it is open a single press runs both handlers and the
//    cancel wins.
//
// Together they pin the shape of `pressEscapeUntil`: press inside the retry
// loop (or a swallowed press wedges the test), and check `settled` immediately
// after each press (or the extra press cancels the confirm).

function Dialog({ ignoreFirst = 0, guarded = false }: { ignoreFirst?: number; guarded?: boolean }) {
  const [open, setOpen] = useState(true);
  const [confirming, setConfirming] = useState(false);
  const swallowed = useRef(0);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      if (swallowed.current < ignoreFirst) {
        swallowed.current += 1;
        return;
      }
      if (guarded) setConfirming(true);
      else setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [ignoreFirst, guarded]);

  if (!open) return null;
  return (
    <>
      <div role="dialog">work item</div>
      {confirming && <DiscardConfirm onCancel={() => setConfirming(false)} />}
    </>
  );
}

function DiscardConfirm({ onCancel }: { onCancel: () => void }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onCancel();
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [onCancel]);
  return <div>Discard unsaved changes?</div>;
}

// Counts every Escape that reaches the document, so a test can assert how many
// presses the loop actually made.
function countEscapes() {
  let presses = 0;
  const onKey = (e: KeyboardEvent) => {
    if (e.key === "Escape") presses += 1;
  };
  document.addEventListener("keydown", onKey);
  return {
    get presses() {
      return presses;
    },
    stop: () => document.removeEventListener("keydown", onKey),
  };
}

afterEach(cleanup);

describe("pressEscapeUntil", () => {
  test("settles even when the first presses are swallowed", async () => {
    render(<Dialog ignoreFirst={3} />);
    const escapes = countEscapes();

    try {
      await pressEscapeUntil(() => {
        expect(screen.queryByRole("dialog")).toBeNull();
      });
      // Three presses lost, so it took four — a swallowed press costs a poll
      // rather than wedging the test.
      expect(escapes.presses).toBe(4);
    } finally {
      escapes.stop();
    }
  });

  test("presses once when nothing is swallowed", async () => {
    render(<Dialog />);
    const escapes = countEscapes();

    try {
      await pressEscapeUntil(() => {
        expect(screen.queryByRole("dialog")).toBeNull();
      });
      expect(escapes.presses).toBe(1);
    } finally {
      escapes.stop();
    }
  });

  test("stops at the first effective press, leaving the discard confirm open", async () => {
    render(<Dialog ignoreFirst={2} guarded />);
    const escapes = countEscapes();

    try {
      await pressEscapeUntil(() => {
        expect(screen.getByText(/Discard unsaved changes/)).toBeTruthy();
      });
      expect(escapes.presses).toBe(3);
      // Still open after the loop returns: no press landed on the confirm.
      expect(screen.getByText(/Discard unsaved changes/)).toBeTruthy();
    } finally {
      escapes.stop();
    }
  });

  test("the shapes it replaces are the ones that fail", async () => {
    // A single press outside the loop is lost when it lands before the
    // listener, and no amount of waiting brings it back.
    render(<Dialog ignoreFirst={1} />);
    fireEvent.keyDown(document, { key: "Escape" });
    await act(async () => {});
    expect(screen.queryByRole("dialog")).toBeTruthy();
    cleanup();

    // A press repeated for good measure cancels the confirm the first one opened.
    render(<Dialog guarded />);
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.getByText(/Discard unsaved changes/)).toBeTruthy();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByText(/Discard unsaved changes/)).toBeNull();
  });
});
