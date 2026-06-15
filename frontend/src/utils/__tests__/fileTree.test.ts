import { describe, expect, test } from "vite-plus/test";
import { buildFileTree } from "../fileTree";
import type { WorktreeFileEntry } from "../../types";

function entry(
  path: string,
  changeStatus: WorktreeFileEntry["changeStatus"] = "none",
): WorktreeFileEntry {
  return { path, changeStatus };
}

describe("buildFileTree", () => {
  test("nests files under their folders", () => {
    const tree = buildFileTree([
      entry("src/app/main.ts"),
      entry("src/app/util.ts"),
      entry("README.md"),
    ]);

    // Folders sort before files at each level.
    expect(tree.map((n) => n.name)).toEqual(["src", "README.md"]);

    const src = tree[0];
    expect(src.type).toBe("folder");
    expect(src.children.map((n) => n.name)).toEqual(["app"]);

    const app = src.children[0];
    expect(app.path).toBe("src/app");
    expect(app.children.map((n) => n.name)).toEqual(["main.ts", "util.ts"]);
    expect(app.children.every((n) => n.type === "file")).toBe(true);
  });

  test("carries each file's change status onto its leaf", () => {
    const tree = buildFileTree([entry("a.ts", "modified"), entry("b.ts", "added")]);
    const byName = Object.fromEntries(tree.map((n) => [n.name, n.changeStatus]));
    expect(byName["a.ts"]).toBe("modified");
    expect(byName["b.ts"]).toBe("added");
  });

  test("sorts deterministically regardless of input order", () => {
    const ordered = buildFileTree([
      entry("z/1.ts"),
      entry("a/2.ts"),
      entry("a/1.ts"),
      entry("top.ts"),
    ]);
    expect(ordered.map((n) => n.name)).toEqual(["a", "z", "top.ts"]);
    expect(ordered[0].children.map((n) => n.name)).toEqual(["1.ts", "2.ts"]);
  });

  test("returns an empty tree for no files", () => {
    expect(buildFileTree([])).toEqual([]);
  });
});
