import { describe, expect, test } from "vite-plus/test";
import { highlightLines, CodeToken } from "../syntaxHighlight";

/** What each line reads as, once its tokens are put back together. */
function texts(lines: CodeToken[][]): string[] {
  return lines.map((tokens) => tokens.map((token) => token.text).join(""));
}

/** The classes a line carries, in order, for the tokens that got one. */
function classes(line: CodeToken[]): (string | undefined)[] {
  return line.map((token) => token.className);
}

/**
 * The invariant every file has to hold whether or not it was highlighted: the
 * viewer numbers and shows exactly what a plain `split("\n")` would, character
 * for character, with only a single trailing newline dropped.
 */
function expectRoundTrip(code: string, path: string) {
  expect(texts(highlightLines(code, path))).toEqual(code.replace(/\n$/, "").split("\n"));
}

describe("highlightLines", () => {
  test("reproduces the file exactly, highlighted or not", () => {
    const code = ["/** Doc. */", "export const n = 1;", "", "function f() {}"].join("\n");
    expectRoundTrip(code, "src/a.ts");
    expectRoundTrip(code, "src/a.unknown-extension");
  });

  test("colours a mapped extension and leaves an unmapped one alone", () => {
    const [line] = highlightLines("const n = 1;", "src/a.ts");
    expect(classes(line)).toContain("hljs-keyword");

    const [plain] = highlightLines("const n = 1;", "src/a.unknown-extension");
    expect(plain).toEqual([{ text: "const n = 1;" }]);
  });

  test("keeps a token spanning several lines coloured on every one of them", () => {
    // The reason the whole file is highlighted before it is cut into lines:
    // tokenizing line by line would end the comment at the first newline.
    const lines = highlightLines("/* one\n   two\n   three */\nlet x;", "a.ts");

    expect(classes(lines[0])).toEqual(["hljs-comment"]);
    expect(classes(lines[1])).toEqual(["hljs-comment"]);
    expect(classes(lines[2])).toEqual(["hljs-comment"]);
    expect(texts(lines)).toEqual(["/* one", "   two", "   three */", "let x;"]);
    expect(classes(lines[3])).toContain("hljs-keyword");
  });

  test("collapses a nested token to its innermost classes", () => {
    // highlight.js nests the substitution inside the template string; the flat
    // line boxes can't hold that nesting, so the inner run keeps its own class —
    // the colour the browser would have resolved for the inner span anyway.
    const [line] = highlightLines("const s = `a${b}c`;", "a.ts");

    const string = line.filter((token) => token.className?.includes("hljs-"));
    expect(string).toContainEqual({ text: "`a", className: "hljs-string" });
    expect(string).toContainEqual({ text: "${b}", className: "hljs-subst" });
    expect(string).toContainEqual({ text: "c`", className: "hljs-string" });
  });

  describe("language from path", () => {
    test("recognises files named for their language, wherever they sit", () => {
      expect(classes(highlightLines("FROM node:20", "docker/Dockerfile")[0])).toContain(
        "hljs-keyword",
      );
      // Makefiles get the makefile grammar, not a shell one: a target line is a
      // section and `$(CC)` a variable, neither of which shell would find.
      const [target, recipe] = highlightLines("all: build\n\t$(CC) -o x x.c", "Makefile");
      expect(classes(target)).toEqual(["hljs-section"]);
      expect(classes(recipe)).toContain("hljs-variable");
    });

    test("highlights both markdown spellings", () => {
      for (const path of ["README.md", "README.markdown", "docs/DEEP.MD"]) {
        expect(classes(highlightLines("# Title\n\ntext", path)[0])).toContain("hljs-section");
      }
    });

    test("leaves a dotfile and a bare name unhighlighted", () => {
      // A leading dot names the file, it does not start an extension, and an
      // extensionless name that isn't in the filename table means nothing here.
      for (const path of [".gitignore", "src/.env", "LICENSE", "docs/NOTICE"]) {
        const lines = highlightLines("node_modules\ndist", path);
        expect(lines.flatMap(classes)).toEqual([undefined, undefined]);
        expect(texts(lines)).toEqual(["node_modules", "dist"]);
      }
    });
  });

  describe("line boundaries", () => {
    test("keeps carriage returns on the lines that carry them", () => {
      const code = "let a = 1;\r\nlet b = 2;\r\n";
      expectRoundTrip(code, "a.ts");
      expect(texts(highlightLines(code, "a.ts"))).toEqual(["let a = 1;\r", "let b = 2;\r"]);
    });

    test("treats one trailing newline as a terminator and a second as a blank line", () => {
      expect(texts(highlightLines("let a;\n", "a.ts"))).toEqual(["let a;"]);
      expect(texts(highlightLines("let a;\n\n", "a.ts"))).toEqual(["let a;", ""]);
      expect(texts(highlightLines("let a;\n\n", "a.unknown-extension"))).toEqual(["let a;", ""]);
    });

    test("gives an empty file a single empty line, highlighted or not", () => {
      expect(highlightLines("", "a.ts")).toEqual([[]]);
      expect(highlightLines("", "a.unknown-extension")).toEqual([[]]);
    });
  });

  test("shows a file too large to highlight rather than stalling on it", () => {
    // Highlighting is one synchronous pass over the whole file; past the cap it
    // is skipped, and the file still renders in full as plain lines.
    const code = `const a = ${"1".repeat(300_001)};\nconst b = 2;`;
    const lines = highlightLines(code, "big.ts");

    expect(lines.flatMap(classes)).toEqual([undefined, undefined]);
    expectRoundTrip(code, "big.ts");
  });
});
