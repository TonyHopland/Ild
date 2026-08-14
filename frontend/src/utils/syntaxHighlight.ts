import { createLowlight } from "lowlight";
import bash from "highlight.js/lib/languages/bash";
import csharp from "highlight.js/lib/languages/csharp";
import css from "highlight.js/lib/languages/css";
import dockerfile from "highlight.js/lib/languages/dockerfile";
import ini from "highlight.js/lib/languages/ini";
import javascript from "highlight.js/lib/languages/javascript";
import json from "highlight.js/lib/languages/json";
import markdown from "highlight.js/lib/languages/markdown";
import python from "highlight.js/lib/languages/python";
import sql from "highlight.js/lib/languages/sql";
import typescript from "highlight.js/lib/languages/typescript";
import xml from "highlight.js/lib/languages/xml";
import yaml from "highlight.js/lib/languages/yaml";

/**
 * The grammars the viewer carries, keyed by the name they are highlighted under.
 * Registering a chosen few rather than highlight.js's `common` set (or all ~190)
 * keeps the bundle to what this repository's worktrees actually contain; an
 * extension with no grammar here is not an error, it just renders unhighlighted.
 */
const lowlight = createLowlight({
  bash,
  csharp,
  css,
  dockerfile,
  ini,
  javascript,
  json,
  markdown,
  python,
  sql,
  typescript,
  xml,
  yaml,
});

/**
 * File extension (lower-case, no dot) to the grammar it is highlighted under.
 * Several extensions share one grammar — highlight.js has no separate TSX or
 * HTML definition, and TOML is close enough to INI that its own author points
 * there.
 */
const LANGUAGE_BY_EXTENSION: Record<string, string> = {
  bash: "bash",
  cjs: "javascript",
  cs: "csharp",
  csproj: "xml",
  css: "css",
  cts: "typescript",
  htm: "xml",
  html: "xml",
  ini: "ini",
  js: "javascript",
  json: "json",
  jsonc: "json",
  jsx: "javascript",
  markdown: "markdown",
  md: "markdown",
  mjs: "javascript",
  mts: "typescript",
  props: "xml",
  py: "python",
  sh: "bash",
  sql: "sql",
  svg: "xml",
  targets: "xml",
  toml: "ini",
  ts: "typescript",
  tsx: "typescript",
  xml: "xml",
  yaml: "yaml",
  yml: "yaml",
  zsh: "bash",
};

/**
 * Past this much text the file is left unhighlighted. Highlighting is one
 * synchronous pass over the whole file, and on the generated bundles, lock files
 * and data dumps that reach this size it is both slow enough to stall the dialog
 * and of no use to anyone reading them.
 */
const MAX_HIGHLIGHT_CHARS = 300_000;

/** Files whose whole name, not extension, names the language. */
const LANGUAGE_BY_FILENAME: Record<string, string> = {
  dockerfile: "dockerfile",
  makefile: "bash",
};

/**
 * One run of characters sharing a highlight class. `text` never spans a line
 * break: {@link highlightLines} has already cut the file's tokens at every
 * newline, so a run can be dropped into a line box as-is.
 */
export interface CodeToken {
  text: string;
  /**
   * The highlight.js class names the run is painted with (`hljs-keyword`,
   * `hljs-string`, …), or undefined for text the grammar did not classify —
   * which the renderer draws as bare text rather than an empty span.
   */
  className?: string;
}

type HighlightNode = ReturnType<typeof lowlight.highlight>["children"][number];

/**
 * Tokenize a file for display, one token list per rendered line.
 *
 * The whole file is highlighted in one pass and the resulting tree is *then* cut
 * at newlines, which is the only order that survives real files: a block
 * comment, template literal, docstring or multi-line JSX attribute is a single
 * token spanning many lines, and tokenizing line by line would end each of them
 * at the first newline and mis-colour everything after it.
 *
 * The language comes from `path`. Anything unmapped, anything past
 * {@link MAX_HIGHLIGHT_CHARS}, and any grammar that throws on the content it is
 * handed all degrade to one unclassified token per line, which renders exactly
 * as an unhighlighted file always has. Callers get lines either way and need no
 * fallback branch of their own.
 */
export function highlightLines(code: string, path: string): CodeToken[][] {
  // A file's single trailing newline is a terminator, not a blank last line, so
  // it is dropped before either path splits — otherwise the viewer numbers a row
  // that isn't there.
  const text = code.replace(/\n$/, "");
  const language = text.length <= MAX_HIGHLIGHT_CHARS ? languageForPath(path) : undefined;

  if (language) {
    try {
      return splitByLine(lowlight.highlight(language, text).children);
    } catch {
      // A grammar that fails on this particular content must not take the
      // viewer with it; the file is still perfectly readable unhighlighted.
    }
  }
  return text.split("\n").map((line) => (line ? [{ text: line }] : []));
}

function languageForPath(path: string): string | undefined {
  const name = path.slice(path.lastIndexOf("/") + 1).toLowerCase();
  const dot = name.lastIndexOf(".");
  // A leading dot names the file (`.gitignore`), it does not start an extension.
  const extension = dot > 0 ? name.slice(dot + 1) : "";
  return LANGUAGE_BY_EXTENSION[extension] ?? LANGUAGE_BY_FILENAME[name];
}

/**
 * Flatten a highlight tree into lines of tokens, splitting every token text at
 * its newlines and carrying the enclosing classes across the break so the second
 * half of a multi-line token keeps its colour.
 *
 * Nested elements (a substitution inside a template string, say) collapse to
 * their innermost classes — the same colour the browser would resolve for the
 * inner span, without the nesting the flat line boxes can't hold.
 */
function splitByLine(nodes: readonly HighlightNode[]): CodeToken[][] {
  const lines: CodeToken[][] = [[]];

  const append = (value: string, className: string | undefined) => {
    const parts = value.split("\n");
    parts.forEach((part, i) => {
      if (i > 0) lines.push([]);
      if (part)
        lines[lines.length - 1].push(className ? { text: part, className } : { text: part });
    });
  };

  const visit = (node: HighlightNode, className: string | undefined) => {
    if (node.type === "text") {
      append(node.value, className);
      return;
    }
    if (node.type !== "element") return;
    const own = node.properties?.className;
    const inner = Array.isArray(own) && own.length > 0 ? own.join(" ") : className;
    for (const child of node.children) visit(child, inner);
  };

  for (const node of nodes) visit(node, undefined);
  return lines;
}
