import { WorktreeFileEntry, WorktreeFileChangeStatus } from "../types";

/**
 * A node in the file explorer tree. Folders carry children; files carry the
 * change status of their backing worktree entry so the tree can badge them.
 */
export interface FileTreeNode {
  name: string;
  path: string;
  type: "file" | "folder";
  changeStatus: WorktreeFileChangeStatus | null;
  children: FileTreeNode[];
}

/**
 * Turn a flat list of worktree files into a nested folder/file tree, the way a
 * Visual-Studio-style explorer renders it. Folders sort before files and both
 * sort case-insensitively, so the order is stable regardless of input order.
 */
export function buildFileTree(files: WorktreeFileEntry[]): FileTreeNode[] {
  const root: FileTreeNode = {
    name: "",
    path: "",
    type: "folder",
    changeStatus: null,
    children: [],
  };

  for (const file of files) {
    const segments = file.path.split("/").filter(Boolean);
    if (segments.length === 0) continue;

    let current = root;
    segments.forEach((segment, index) => {
      const isLeaf = index === segments.length - 1;
      const path = segments.slice(0, index + 1).join("/");
      let child = current.children.find(
        (c) => c.name === segment && c.type === (isLeaf ? "file" : "folder"),
      );
      if (!child) {
        child = {
          name: segment,
          path,
          type: isLeaf ? "file" : "folder",
          changeStatus: isLeaf ? file.changeStatus : null,
          children: [],
        };
        current.children.push(child);
      }
      current = child;
    });
  }

  sortTree(root.children);
  return root.children;
}

function sortTree(nodes: FileTreeNode[]): void {
  nodes.sort((a, b) => {
    if (a.type !== b.type) return a.type === "folder" ? -1 : 1;
    return a.name.localeCompare(b.name, undefined, { sensitivity: "base" });
  });
  for (const node of nodes) {
    if (node.children.length > 0) sortTree(node.children);
  }
}
