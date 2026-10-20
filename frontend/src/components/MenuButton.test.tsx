import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import MenuButton, { type MenuItem } from "./MenuButton";

afterEach(cleanup);

function renderMenu(items: Partial<MenuItem>[] = []) {
  const full: MenuItem[] = [
    { id: "a", label: "First", onSelect: vi.fn() },
    { id: "b", label: "Second", onSelect: vi.fn(), disabled: true },
    { id: "c", label: "Third", onSelect: vi.fn() },
    ...items.map((i, n) => ({ id: `x${n}`, label: `Extra ${n}`, onSelect: vi.fn(), ...i })),
  ];
  render(<MenuButton label="More actions" items={full} />);
  return { items: full, button: screen.getByRole("button", { name: "More actions" }) };
}

describe("MenuButton", () => {
  test("opening moves focus to the first item; the arrows, Home and End skip what cannot be chosen", () => {
    const { button } = renderMenu();
    fireEvent.click(button);

    const item = (name: string) => screen.getByRole("menuitem", { name });
    expect(document.activeElement).toBe(item("First"));
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(document.activeElement).toBe(item("Third"));
    fireEvent.keyDown(document.activeElement!, { key: "ArrowDown" });
    expect(document.activeElement).toBe(item("First"));
    fireEvent.keyDown(document.activeElement!, { key: "ArrowUp" });
    expect(document.activeElement).toBe(item("Third"));
    fireEvent.keyDown(document.activeElement!, { key: "Home" });
    expect(document.activeElement).toBe(item("First"));
    fireEvent.keyDown(document.activeElement!, { key: "End" });
    expect(document.activeElement).toBe(item("Third"));
  });

  test("Escape closes it and gives focus back to the button, without reaching the dialog around it", () => {
    const outer = vi.fn();
    document.addEventListener("keydown", outer);
    const { button } = renderMenu();
    fireEvent.click(button);

    fireEvent.keyDown(screen.getByRole("menuitem", { name: "First" }), { key: "Escape" });

    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(button);
    expect(outer).not.toHaveBeenCalled();
    document.removeEventListener("keydown", outer);
  });

  test("Tab closes it, and choosing an item runs it and gives focus back to the button", () => {
    const { button, items } = renderMenu();
    fireEvent.click(button);
    fireEvent.keyDown(screen.getByRole("menuitem", { name: "First" }), { key: "Tab" });
    expect(screen.queryByRole("menu")).toBeNull();

    fireEvent.keyDown(button, { key: "ArrowDown" });
    fireEvent.click(screen.getByRole("menuitem", { name: "Third" }));
    expect(items[2].onSelect).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("menu")).toBeNull();
    expect(document.activeElement).toBe(button);
  });

  test("two items with the same label are still two items", () => {
    renderMenu([{ label: "First" }]);
    fireEvent.click(screen.getByRole("button", { name: "More actions" }));
    expect(screen.getAllByRole("menuitem", { name: "First" })).toHaveLength(2);
  });
});
