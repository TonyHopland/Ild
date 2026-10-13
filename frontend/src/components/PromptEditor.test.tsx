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
    expect(listItems.length).toBe(13);
  });

  test("typing {{Conversation. filters to the three Conversation placeholders", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 15);
    fireEvent.change(textarea, { target: { value: "{{Conversation." } });

    const listItems = screen.getAllByRole("listitem");
    expect(listItems.length).toBe(3);
    expect(listItems[0].textContent).toContain("Conversation.Full");
    expect(listItems[1].textContent).toContain("Conversation.AI");
    expect(listItems[2].textContent).toContain("Conversation.Human");
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

    expect(screen.getAllByRole("listitem").length).toBe(13);

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

    expect(screen.getAllByRole("listitem").length).toBe(13);

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

    const items = screen.getAllByRole("listitem");
    const last = items[items.length - 1].textContent!.match(/\{\{[^}]*\}\}/)![0];

    fireEvent.keyDown(textarea, { key: "ArrowUp" });
    fireEvent.keyDown(textarea, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith(last);
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

  test("typing {{Gl offers the global-variable placeholder", () => {
    render(<PromptEditor value="" onChange={vi.fn()} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 4);
    fireEvent.change(textarea, { target: { value: "{{Gl" } });

    const listItems = screen.getAllByRole("listitem");
    expect(listItems.length).toBe(1);
    expect(listItems[0].textContent).toContain("{{Global.}}");
  });

  test("offers one entry per existing global variable, filtered and inserted like the rest", () => {
    const onChange = vi.fn();
    render(<PromptEditor value="" onChange={onChange} globalNames={["review_rules", "writing"]} />);

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    setCursor(textarea, 2);
    fireEvent.change(textarea, { target: { value: "{{" } });
    const all = screen.getAllByRole("listitem").map((li) => li.textContent);
    expect(all.length).toBe(15);
    expect(all.some((t) => t?.includes("{{Global.review_rules}}"))).toBe(true);
    expect(all.some((t) => t?.includes("{{Global.writing}}"))).toBe(true);

    setCursor(textarea, 4);
    fireEvent.change(textarea, { target: { value: "{{Gl" } });
    const globals = screen.getAllByRole("listitem");
    expect(globals.map((li) => li.textContent!.match(/\{\{[^}]*\}\}/)![0]).sort()).toEqual([
      "{{Global.review_rules}}",
      "{{Global.writing}}",
      "{{Global.}}",
    ]);

    setCursor(textarea, 11);
    fireEvent.change(textarea, { target: { value: "{{Global.re" } });
    const narrowed = screen.getAllByRole("listitem");
    expect(narrowed.length).toBe(1);
    fireEvent.mouseDown(narrowed[0]);

    expect(onChange).toHaveBeenLastCalledWith("{{Global.review_rules}}");
  });
});
