import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import Sheet from "./Sheet";

afterEach(() => {
  cleanup();
  document.getElementById("root")?.remove();
});

function renderSheet() {
  const root = document.createElement("div");
  root.id = "root";
  document.body.appendChild(root);
  const opener = document.createElement("button");
  opener.textContent = "Open";
  root.appendChild(opener);
  opener.focus();
  const onClose = vi.fn();
  const view = render(
    <Sheet title="Filters" onClose={onClose}>
      <button type="button">One</button>
      <button type="button">Two</button>
    </Sheet>,
    { container: root.appendChild(document.createElement("div")) },
  );
  return { root, opener, onClose, view };
}

describe("Sheet", () => {
  test("makes the page behind it inert while it is open, and gives it back on close", () => {
    const { root, opener, view } = renderSheet();
    const sheet = screen.getByRole("dialog", { name: "Filters" });
    expect(root.hasAttribute("inert")).toBe(true);
    expect(root.contains(sheet)).toBe(false);

    view.unmount();
    expect(root.hasAttribute("inert")).toBe(false);
    expect(document.activeElement).toBe(opener);
  });

  test("Tab and Shift+Tab go round its own controls", () => {
    renderSheet();
    const one = screen.getByRole("button", { name: "One" });
    const two = screen.getByRole("button", { name: "Two" });

    two.focus();
    fireEvent.keyDown(two, { key: "Tab" });
    expect(document.activeElement).toBe(one);
    fireEvent.keyDown(one, { key: "Tab", shiftKey: true });
    expect(document.activeElement).toBe(two);
  });

  test("Escape closes it", () => {
    const { onClose } = renderSheet();
    fireEvent.keyDown(window, { key: "Escape" });
    expect(onClose).toHaveBeenCalled();
  });
});
