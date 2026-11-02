import { describe, expect, test } from "vite-plus/test";
import { opensInNewTab } from "../linkTarget";

const app = "http://ild.test:3100/taskboard";

describe("opensInNewTab", () => {
  test.each([
    ["https://example.com/page", true],
    ["http://wi-42.ild.test:3100/", true],
    ["http://wi-42.ild.test/", true],
    ["http://ild.test:5100/", true],
    ["http://ild.test/", true],
    ["//other.example/x", true],
    ["/taskboard/42", false],
    ["taskboard", false],
    ["./x", false],
    ["?q=1", false],
    ["http://ild.test:3100/anything", false],
    ["https://ild.test:3100/anything", false],
    ["#section", false],
    ["#user-content-fn-1", false],
    ["mailto:a@example.com", false],
    ["tel:+123", false],
    ["ftp://example.com/file", false],
    ["", false],
    [undefined, false],
  ])("%s opens in a new tab: %s", (href, expected) => {
    expect(opensInNewTab(href, app)).toBe(expected);
  });

  test("a default port written out is the same host", () => {
    expect(opensInNewTab("http://ild.test:80/x", "http://ild.test/")).toBe(false);
    expect(opensInNewTab("http://ild.test/x", "http://ild.test:80/")).toBe(false);
  });

  test("an href that cannot be parsed does not open in a new tab", () => {
    expect(opensInNewTab("http://", app)).toBe(false);
  });
});
