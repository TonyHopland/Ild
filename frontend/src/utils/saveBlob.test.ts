// @vitest-environment jsdom
import { afterEach, beforeEach, expect, test, vi } from "vite-plus/test";
import { REVOKE_DELAY_MS, saveBlob } from "./saveBlob";

const createObjectURL = vi.fn(() => "blob:example-url");
const revokeObjectURL = vi.fn();

beforeEach(() => {
  vi.useFakeTimers();
  URL.createObjectURL = createObjectURL;
  URL.revokeObjectURL = revokeObjectURL;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

test("clicks a temporary download link for the bytes and removes it, then revokes the URL only after the delay", () => {
  const blob = new Blob(["hello"]);
  const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
    this: HTMLAnchorElement,
  ) {
    expect(document.body.contains(this)).toBe(true);
  });

  saveBlob(blob, "report.txt");

  expect(createObjectURL).toHaveBeenCalledWith(blob);
  expect(click).toHaveBeenCalledTimes(1);
  const link = click.mock.contexts[0] as HTMLAnchorElement;
  expect(link.getAttribute("href")).toBe("blob:example-url");
  expect(link.download).toBe("report.txt");
  expect(document.body.contains(link)).toBe(false);

  vi.advanceTimersByTime(REVOKE_DELAY_MS - 1);
  expect(revokeObjectURL).not.toHaveBeenCalled();
  vi.advanceTimersByTime(1);
  expect(revokeObjectURL).toHaveBeenCalledWith("blob:example-url");
});
