import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import PromptEditor, { PLACEHOLDERS } from "./PromptEditor";

function setCursor(textarea: HTMLTextAreaElement, pos: number) {
  textarea.selectionStart = pos;
  textarea.selectionEnd = pos;
}

afterEach(() => {
  cleanup();
});

/** Types `text` with the cursor at its end and returns the box. */
function typeInto(text: string, props: { globalNames?: string[] } = {}) {
  const onChange = vi.fn();
  render(<PromptEditor value="" onChange={onChange} {...props} />);
  const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
  setCursor(textarea, text.length);
  fireEvent.change(textarea, { target: { value: text } });
  return { textarea, onChange };
}

const suggestions = () => screen.getAllByRole("listitem").map((li) => li.textContent ?? "");

describe("PromptEditor", () => {
  test("typing {{ shows every placeholder suggestion", () => {
    typeInto("{{");

    expect(suggestions()).toHaveLength(PLACEHOLDERS.length);
  });

  test.each([
    ["{{Conversation.", ["Conversation.Full", "Conversation.AI", "Conversation.Human"]],
    ["{{Var", ["{{Var.}}"]],
    ["{{WorkItem.", ["WorkItem.Title", "WorkItem.Description"]],
    ["{{Eve", ["EventLog.Summary", "EventLog.LastN"]],
    ["{{Gl", ["{{Global.}}"]],
  ])("typing %s narrows the suggestions in order", (prefix, expected) => {
    typeInto(prefix);

    const shown = suggestions();
    expect(shown).toHaveLength(expected.length);
    expected.forEach((text, i) => expect(shown[i]).toContain(text));
  });

  test.each([["{{NonExistent.Foo"], ["just plain text"]])(
    "typing %s shows no suggestions",
    (text) => {
      typeInto(text);

      expect(screen.queryByRole("list")).toBeNull();
    },
  );

  test.each([["Enter"], ["Tab"]])("pressing %s inserts the selected placeholder", (key) => {
    const { textarea, onChange } = typeInto("{{");

    fireEvent.keyDown(textarea, { key });

    expect(onChange).toHaveBeenCalledWith("{{WorkItem.Title}}");
  });

  test("pressing Escape hides suggestions", () => {
    const { textarea } = typeInto("{{");

    fireEvent.keyDown(textarea, { key: "Escape" });

    expect(screen.queryByRole("list")).toBeNull();
  });

  test("ArrowDown cycles through suggestions", () => {
    const { textarea, onChange } = typeInto("{{");

    fireEvent.keyDown(textarea, { key: "ArrowDown" });
    fireEvent.keyDown(textarea, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith("{{WorkItem.Description}}");
  });

  test("ArrowUp cycles backwards to the last suggestion", () => {
    const { textarea, onChange } = typeInto("{{");
    const items = screen.getAllByRole("listitem");
    const last = items[items.length - 1].textContent!.match(/\{\{[^}]*\}\}/)![0];

    fireEvent.keyDown(textarea, { key: "ArrowUp" });
    fireEvent.keyDown(textarea, { key: "Enter" });

    expect(onChange).toHaveBeenCalledWith(last);
  });

  test("clicking a suggestion inserts it", () => {
    const { onChange } = typeInto("{{");

    const item = screen
      .getAllByRole("listitem")
      .find((li) => li.textContent?.includes("EventLog.Summary"));
    fireEvent.mouseDown(item!);

    expect(onChange).toHaveBeenCalledWith("{{EventLog.Summary}}");
  });

  test("offers one entry per existing global variable, filtered and inserted like the rest", () => {
    const globalNames = ["review_rules", "writing"];
    const { textarea, onChange } = typeInto("{{", { globalNames });
    const all = suggestions();
    expect(all).toHaveLength(PLACEHOLDERS.length + globalNames.length);
    expect(all.some((t) => t.includes("{{Global.review_rules}}"))).toBe(true);
    expect(all.some((t) => t.includes("{{Global.writing}}"))).toBe(true);

    setCursor(textarea, 4);
    fireEvent.change(textarea, { target: { value: "{{Gl" } });
    expect(
      suggestions()
        .map((t) => t.match(/\{\{[^}]*\}\}/)![0])
        .sort(),
    ).toEqual(["{{Global.review_rules}}", "{{Global.writing}}", "{{Global.}}"]);

    setCursor(textarea, 11);
    fireEvent.change(textarea, { target: { value: "{{Global.re" } });
    const narrowed = screen.getAllByRole("listitem");
    expect(narrowed).toHaveLength(1);
    fireEvent.mouseDown(narrowed[0]);

    expect(onChange).toHaveBeenLastCalledWith("{{Global.review_rules}}");
  });
});
