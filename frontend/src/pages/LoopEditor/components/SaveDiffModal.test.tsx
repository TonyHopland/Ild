import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import SaveDiffModal from "./SaveDiffModal";

afterEach(cleanup);

const saved = JSON.stringify(
  { $schema: "ild-loop-template/v1", name: "Loop", prompt: "old" },
  null,
  2,
);
const edited = JSON.stringify(
  { $schema: "ild-loop-template/v1", name: "Loop", prompt: "new" },
  null,
  2,
);

describe("SaveDiffModal", () => {
  test("renders nothing when closed", () => {
    const { container } = render(
      <SaveDiffModal
        isOpen={false}
        beforeJson={saved}
        afterJson={edited}
        isSaving={false}
        onConfirm={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    expect(container.firstChild).toBeNull();
  });

  test("shows the whole-document diff of saved vs edited JSON", () => {
    render(
      <SaveDiffModal
        isOpen
        beforeJson={saved}
        afterJson={edited}
        isSaving={false}
        onConfirm={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    const view = screen.getByTestId("save-diff-view");
    // The changed prompt line appears both removed (old) and added (new).
    expect(view.querySelector(".save-diff-del")?.textContent).toContain("old");
    expect(view.querySelector(".save-diff-add")?.textContent).toContain("new");
  });

  test("Save changes triggers onConfirm (persist)", () => {
    const onConfirm = vi.fn();
    render(
      <SaveDiffModal
        isOpen
        beforeJson={saved}
        afterJson={edited}
        isSaving={false}
        onConfirm={onConfirm}
        onCancel={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByText("Save changes"));
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  test("Cancel aborts without persisting", () => {
    const onConfirm = vi.fn();
    const onCancel = vi.fn();
    render(
      <SaveDiffModal
        isOpen
        beforeJson={saved}
        afterJson={edited}
        isSaving={false}
        onConfirm={onConfirm}
        onCancel={onCancel}
      />,
    );
    fireEvent.click(screen.getByText("Cancel"));
    expect(onCancel).toHaveBeenCalledTimes(1);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  test("reports no changes when saved and edited match", () => {
    render(
      <SaveDiffModal
        isOpen
        beforeJson={saved}
        afterJson={saved}
        isSaving={false}
        onConfirm={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    expect(screen.getByTestId("save-diff-empty")).toBeTruthy();
  });

  test("buttons are disabled while saving", () => {
    render(
      <SaveDiffModal
        isOpen
        beforeJson={saved}
        afterJson={edited}
        isSaving
        onConfirm={vi.fn()}
        onCancel={vi.fn()}
      />,
    );
    expect((screen.getByText("Saving…") as HTMLButtonElement).disabled).toBeTruthy();
    expect((screen.getByText("Cancel") as HTMLButtonElement).disabled).toBeTruthy();
  });
});
