// @vitest-environment jsdom
import { afterEach, beforeEach, expect, test, vi } from "vite-plus/test";
import { REVOKE_DELAY_MS, saveBlob } from "./saveBlob";

beforeEach(() => {
  vi.useFakeTimers();
  URL.createObjectURL = vi.fn(() => "blob:example-url");
  URL.revokeObjectURL = vi.fn();
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

test("clicks a temporary download link for the bytes and removes it, then revokes the URL only after the delay", () => {
  const blob = new Blob(["hello"]);
  let clicked: HTMLAnchorElement | null = null;
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
    this: HTMLAnchorElement,
  ) {
    clicked = this;
    expect(document.body.contains(this)).toBe(true);
  });

  saveBlob(blob, "report.txt");

  expect(URL.createObjectURL).toHaveBeenCalledWith(blob);
  expect(clicked).not.toBeNull();
  const link = clicked as unknown as HTMLAnchorElement;
  expect(link.getAttribute("href")).toBe("blob:example-url");
  expect(link.download).toBe("report.txt");
  expect(document.body.contains(link)).toBe(false);

  vi.advanceTimersByTime(REVOKE_DELAY_MS - 1);
  expect(URL.revokeObjectURL).not.toHaveBeenCalled();
  vi.advanceTimersByTime(1);
  expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:example-url");
});
