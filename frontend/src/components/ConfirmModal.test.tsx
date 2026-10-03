import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import ConfirmModal from "./ConfirmModal";

afterEach(cleanup);

describe("ConfirmModal as the delete dialogs use it", () => {
  test("does not focus its confirm button, Enter on it deletes nothing, and Escape cancels once", () => {
    const onConfirm = vi.fn();
    const onCancel = vi.fn();
    const onDocumentKey = vi.fn();
    document.addEventListener("keydown", onDocumentKey);
    render(
      <ConfirmModal
        isOpen
        title="Delete output"
        message="Delete it?"
        onConfirm={onConfirm}
        onCancel={onCancel}
      />,
    );

    const confirm = screen.getByRole("button", { name: "Delete" });
    expect(document.activeElement).not.toBe(confirm);

    fireEvent.keyDown(confirm, { key: "Enter", code: "Enter" });
    fireEvent.keyDown(confirm, { key: "Enter", code: "Enter", repeat: true });
    expect(onConfirm).not.toHaveBeenCalled();

    fireEvent.keyDown(document, { key: "Escape", code: "Escape" });
    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(onDocumentKey.mock.calls.some(([e]) => (e as KeyboardEvent).key === "Escape")).toBe(
      true,
    );
    document.removeEventListener("keydown", onDocumentKey);
  });
});
