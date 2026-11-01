import { afterEach, describe, expect, test } from "vite-plus/test";
import { render, cleanup } from "@testing-library/react";
import MarkdownRenderer from "./MarkdownRenderer";

afterEach(cleanup);

function otherPort(): string {
  const current = Number(
    window.location.port || (window.location.protocol === "https:" ? 443 : 80),
  );
  return String(current === 4999 ? 5000 : 4999);
}

function link(markdown: string, text = "go"): HTMLAnchorElement {
  const { container } = render(<MarkdownRenderer content={markdown} />);
  const a = Array.from(container.querySelectorAll("a")).find((x) => x.textContent === text);
  if (!a) throw new Error(`no link "${text}" in ${container.innerHTML}`);
  return a as HTMLAnchorElement;
}

function expectNewTab(a: HTMLAnchorElement) {
  expect(a.getAttribute("target")).toBe("_blank");
  const rel = (a.getAttribute("rel") ?? "").split(/\s+/);
  expect(rel).toContain("noopener");
  expect(rel).toContain("noreferrer");
}

describe("MarkdownRenderer links", () => {
  test("a link to another site opens in a new tab, text and href unchanged", () => {
    const a = link("[go](https://example.com/page)");
    expectNewTab(a);
    expect(a.getAttribute("href")).toBe("https://example.com/page");
  });

  test("a bare URL in plain text is autolinked and opens in a new tab", () => {
    const { container } = render(<MarkdownRenderer content="see https://example.com now" />);
    const a = container.querySelector("a") as HTMLAnchorElement;
    expectNewTab(a);
    expect(a.textContent).toBe("https://example.com");
  });

  test("a link to a subdomain of the app's hostname (a preview) opens in a new tab", () => {
    const url = `${window.location.protocol}//wi-42.${window.location.hostname}/`;
    const a = link(`[go](${url})`);
    expectNewTab(a);
    expect(a.getAttribute("href")).toBe(url);
  });

  test("a link to the app's hostname on a different port opens in a new tab", () => {
    expectNewTab(
      link(`[go](${window.location.protocol}//${window.location.hostname}:${otherPort()}/)`),
    );
  });

  test.each([
    ["a relative path", "/taskboard/42"],
    ["a bare relative path", "taskboard"],
    ["a dot-relative path", "./x"],
    ["a query-only href", "?q=1"],
    ["a fragment", "#section"],
    ["mailto", "mailto:a@example.com"],
    ["tel", "tel:+123"],
  ])("%s has no target", (_name, href) => {
    const a = link(`[go](${href})`);
    expect(a.hasAttribute("target")).toBe(false);
  });

  test("an absolute URL with exactly the app's hostname and port has no target", () => {
    const url = `${window.location.origin}/taskboard/42`;
    const a = link(`[go](${url})`);
    expect(a.hasAttribute("target")).toBe(false);
    expect(a.getAttribute("href")).toBe(url);
  });

  test("a footnote reference stays in the same tab", () => {
    const { container } = render(<MarkdownRenderer content={"Claim[^1]\n\n[^1]: The note."} />);
    const anchors = Array.from(container.querySelectorAll("a"));
    expect(anchors.length).toBeGreaterThan(0);
    for (const a of anchors) {
      expect(a.getAttribute("href")?.startsWith("#")).toBe(true);
      expect(a.hasAttribute("target")).toBe(false);
    }
  });

  test("a javascript: URL keeps its sanitised, non-executable href", () => {
    const { container } = render(<MarkdownRenderer content="[go](javascript:alert(1))" />);
    const a = container.querySelector("a");
    expect(a?.getAttribute("href") ?? "").not.toMatch(/^javascript:/i);
  });

  test("non-link markdown renders as before", () => {
    const { container } = render(<MarkdownRenderer content={"**bold** and `code`"} />);
    expect(container.querySelector("strong")?.textContent).toBe("bold");
    expect(container.querySelector("code")?.textContent).toBe("code");
  });
});
