import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import PromptEditor from "./PromptEditor";

function setCursor(textarea: HTMLTextAreaElement, pos: number) {
  textarea.selectionStart = pos;
  textarea.selectionEnd = pos;
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

describe("PromptEditor", () => {
  test("renders a textarea and passes value/onChange", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="hello" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    expect(textarea.value).toBe("hello");

    fireEvent.change(textarea, { target: { value: "hello world" } });
    expect(onChange).toHaveBeenCalledWith("hello world");
  });

  test("typing {{ shows all placeholder suggestions", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });

    const listItems = screen.getAllByRole("listitem");
    expect(listItems.length).toBe(9);
  });

  test("typing {{Var filters to the loop-variable placeholder", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 5);
    fireEvent.change(textarea, { target: { value: "{{Var" } });

    const listItems = screen.getAllByRole("listitem");
    expect(listItems.length).toBe(1);
    expect(listItems[0].textContent).toContain("{{Var.}}");
    expect(listItems[0].textContent).toContain("loop variable");
  });

  test("typing {{WorkItem. filters to WorkItem placeholders", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 11);
    fireEvent.change(textarea, { target: { value: "{{WorkItem." } });

    const listItems = screen.getAllByRole("listitem");
    expect(listItems.length).toBe(2);
    expect(listItems[0].textContent).toContain("WorkItem.Title");
    expect(listItems[1].textContent).toContain("WorkItem.Description");
  });

  test("typing {{Eve filters to EventLog placeholders", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 5);
    fireEvent.change(textarea, { target: { value: "{{Eve" } });

    const listItems = screen.getAllByRole("listitem");
    expect(listItems.length).toBe(2);
    expect(listItems[0].textContent).toContain("EventLog.Summary");
    expect(listItems[1].textContent).toContain("EventLog.LastN");
  });

  test("typing a non-matching prefix hides suggestions", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 17);
    fireEvent.change(textarea, { target: { value: "{{NonExistent.Foo" } });

    expect(screen.queryByRole("list")).toBeNull();
  });

  test("typing without {{ does not show suggestions", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 15);
    fireEvent.change(textarea, { target: { value: "just plain text" } });

    expect(screen.queryByRole("list")).toBeNull();
  });

  test("pressing Enter inserts the selected placeholder", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });

    expect(screen.getAllByRole("listitem").length).toBe(9);

    fireEvent.keyDown(textarea, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith("{{WorkItem.Title}}");
  });

  test("pressing Tab inserts the selected placeholder", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });

    fireEvent.keyDown(textarea, { key: "Tab" });

    expect(onChange).toHaveBeenCalledWith("{{WorkItem.Title}}");
  });

  test("pressing Escape hides suggestions", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });

    expect(screen.getAllByRole("listitem").length).toBe(9);

    fireEvent.keyDown(textarea, { key: "Escape" });

    expect(screen.queryByRole("list")).toBeNull();
  });

  test("ArrowDown cycles through suggestions", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });

    fireEvent.keyDown(textarea, { key: "ArrowDown" });
    fireEvent.keyDown(textarea, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith("{{WorkItem.Description}}");
  });

  test("ArrowUp cycles backwards through suggestions", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });

    fireEvent.keyDown(textarea, { key: "ArrowUp" });
    fireEvent.keyDown(textarea, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith("{{Var.}}");
  });

  test("clicking a suggestion inserts it", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });

    // Find the EventLog.Summary item by its key span
    const items = screen.getAllByRole("listitem");
    const eventLogItem = items.find((item) => item.textContent?.includes("EventLog.Summary"));
    expect(eventLogItem).toBeTruthy();
    fireEvent.mouseDown(eventLogItem!);

    expect(onChange).toHaveBeenCalledWith("{{EventLog.Summary}}");
  });
});
