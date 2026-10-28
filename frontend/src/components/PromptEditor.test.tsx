import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import PromptEditor from "./PromptEditor";

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

/** The placeholder each suggestion inserts, in the order they are listed. */
const suggestedKeys = () => suggestions().map((text) => text.match(/\{\{[^}]*\}\}/)![0]);

const BUILT_IN = [
  "{{WorkItem.Title}}",
  "{{WorkItem.Description}}",
  "{{EventLog.Summary}}",
  "{{EventLog.LastN}}",
  "{{Node.Input}}",
  "{{PreviousNode.Output}}",
  "{{Conversation.Full}}",
  "{{Conversation.AI}}",
  "{{Conversation.Human}}",
  "{{WorkTree.Diff}}",
  "{{WorkTree.File:}}",
  "{{Var.}}",
  "{{Global.}}",
];

describe("PromptEditor", () => {
  test("typing {{ shows every placeholder suggestion", () => {
    typeInto("{{");

    expect(suggestedKeys()).toEqual(BUILT_IN);
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
    expect(suggestedKeys()).toEqual([...BUILT_IN, "{{Global.review_rules}}", "{{Global.writing}}"]);

    setCursor(textarea, 4);
    fireEvent.change(textarea, { target: { value: "{{Gl" } });
    expect(suggestedKeys().sort()).toEqual([
      "{{Global.review_rules}}",
      "{{Global.writing}}",
      "{{Global.}}",
    ]);

    setCursor(textarea, 11);
    fireEvent.change(textarea, { target: { value: "{{Global.re" } });
    const narrowed = screen.getAllByRole("listitem");
    expect(narrowed).toHaveLength(1);
    fireEvent.mouseDown(narrowed[0]);

    expect(onChange).toHaveBeenLastCalledWith("{{Global.review_rules}}");
  });
});
