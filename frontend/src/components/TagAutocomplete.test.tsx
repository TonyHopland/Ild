import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import TagAutocomplete from "./TagAutocomplete";

afterEach(() => {
  cleanup();
});

const OPTIONS = ["alpha", "beta", "gamma"];

function renderTag(value = "", options = OPTIONS) {
  const onChange = vi.fn();
  render(<TagAutocomplete id="tags" value={value} onChange={onChange} options={options} />);
  return { onChange, input: screen.getByRole("textbox") as HTMLInputElement };
}

describe("TagAutocomplete", () => {
  test("disables the browser's native autofill overlay", () => {
    const { input } = renderTag();
    expect(input.getAttribute("autocomplete")).toBe("off");
    expect(input.getAttribute("autocorrect")).toBe("off");
    expect(input.getAttribute("autocapitalize")).toBe("off");
    expect(input.getAttribute("spellcheck")).toBe("false");
    // Opt out of common password managers, which render their own overlays.
    expect(input.hasAttribute("data-1p-ignore")).toBe(true);
    expect(input.getAttribute("data-lpignore")).toBe("true");
  });

  test("still renders Ild's own suggestions on focus", () => {
    const { input } = renderTag();
    fireEvent.focus(input);
    // getByText throws if the suggestion is not rendered.
    expect(screen.getByText("alpha")).toBeTruthy();
    expect(screen.getByText("beta")).toBeTruthy();
  });
});
