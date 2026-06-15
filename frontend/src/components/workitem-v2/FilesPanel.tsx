import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  WorkItem,
  WorktreeFileChangeStatus,
  WorktreeFileContent,
  WorktreeFileEntry,
} from "../../types";
import { workItemService } from "../../services/auth";
import { buildFileTree, FileTreeNode } from "../../utils/fileTree";

const STATUS_BADGE: Record<Exclude<WorktreeFileChangeStatus, "none">, string> = {
  added: "A",
  modified: "M",
  deleted: "D",
};

/**
 * Files tab: a Visual-Studio-style explorer with a file tree on the left and a
 * read-only viewer on the right. The tree toggles between every file ("All")
 * and only files that differ from the base branch ("Changes", PR style); the
 * viewer toggles between the full file ("Code") and its unified diff ("Diff").
 */
export default function FilesPanel({ workItem }: { workItem: WorkItem }) {
  const [files, setFiles] = useState<WorktreeFileEntry[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [changesOnly, setChangesOnly] = useState(false);
  // Folder expansion defaults differ by scope: the "All files" view starts
  // collapsed (VS-style), while the "Changes" view starts expanded (PR-style).
  // This set holds the folders the user has flipped away from the current
  // view's default; it survives the background refreshes below (so manual
  // choices and newly appearing folders behave) and is reset when the scope
  // changes. A folder is therefore open when its presence in the set differs
  // from the view's default-open state.
  const [toggledFolders, setToggledFolders] = useState<Set<string>>(new Set());

  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const selectedPathRef = useRef<string | null>(null);
  const [content, setContent] = useState<WorktreeFileContent | null>(null);
  const [contentLoading, setContentLoading] = useState(false);
  const [contentError, setContentError] = useState<string | null>(null);
  const [showDiff, setShowDiff] = useState(false);

  const refresh = useCallback(
    async (showLoading: boolean) => {
      if (!workItem.worktreePath) {
        setFiles([]);
        return;
      }
      if (showLoading) setLoading(true);
      setError(null);
      try {
        const result = await workItemService.getFiles(workItem.id);
        setFiles(Array.isArray(result?.files) ? result.files : []);
      } catch (e) {
        setError((e as { message?: string })?.message ?? "Failed to load files.");
        setFiles([]);
      } finally {
        if (showLoading) setLoading(false);
      }
    },
    [workItem.id, workItem.worktreePath],
  );

  const loadContent = useCallback(
    async (path: string, showLoading: boolean) => {
      setContentError(null);
      if (showLoading) {
        setContent(null);
        setContentLoading(true);
      }
      try {
        const result = await workItemService.getFileContent(workItem.id, path);
        setContent(result);
      } catch (e) {
        setContentError((e as { message?: string })?.message ?? "Failed to load file.");
      } finally {
        if (showLoading) setContentLoading(false);
      }
    },
    [workItem.id],
  );

  // The parent refetches the work item every time the run advances (node/run
  // state changes) and passes down a fresh object, so re-pull the file list and
  // the open file whenever the work item updates. This keeps the explorer in
  // sync with the worktree without a manual page refresh. The first load (and
  // switching to a different item) shows the loading state; later background
  // refreshes are silent so the tree and viewer don't flicker.
  const lastKeyRef = useRef<string | null>(null);
  useEffect(() => {
    const key = `${workItem.id}:${workItem.worktreePath ?? ""}`;
    const isNewItem = lastKeyRef.current !== key;
    lastKeyRef.current = key;
    void refresh(isNewItem);
    if (!isNewItem && selectedPathRef.current) {
      void loadContent(selectedPathRef.current, false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workItem]);

  const changedCount = useMemo(
    () => files.filter((f) => f.changeStatus !== "none").length,
    [files],
  );

  const visibleFiles = useMemo(
    () => (changesOnly ? files.filter((f) => f.changeStatus !== "none") : files),
    [files, changesOnly],
  );

  const tree = useMemo(() => buildFileTree(visibleFiles), [visibleFiles]);

  const selectFile = useCallback(
    (path: string) => {
      setSelectedPath(path);
      selectedPathRef.current = path;
      void loadContent(path, true);
    },
    [loadContent],
  );

  const toggleFolder = useCallback((path: string) => {
    setToggledFolders((prev) => {
      const next = new Set(prev);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }, []);

  // Switching scope resets folders to that view's default expansion.
  const selectScope = useCallback(
    (next: boolean) => {
      if (next === changesOnly) return;
      setChangesOnly(next);
      setToggledFolders(new Set());
    },
    [changesOnly],
  );

  // A folder is open when its toggled state differs from the view's default:
  // collapsed by default for "All files", expanded by default for "Changes".
  const isFolderExpanded = useCallback(
    (path: string) => changesOnly !== toggledFolders.has(path),
    [changesOnly, toggledFolders],
  );

  if (!workItem.worktreePath) {
    return (
      <div className="wiv2-empty">
        No worktree — the file explorer is only available for items with an active worktree.
      </div>
    );
  }

  const renderNodes = (nodes: FileTreeNode[], depth: number): React.ReactNode =>
    nodes.map((node) => {
      const indent = { paddingLeft: `${depth * 0.85 + 0.5}rem` };
      if (node.type === "folder") {
        const isExpanded = isFolderExpanded(node.path);
        return (
          <div key={`d:${node.path}`}>
            <button
              type="button"
              className="wiv2-file-row wiv2-file-folder"
              style={indent}
              onClick={() => toggleFolder(node.path)}
              aria-expanded={isExpanded}
            >
              <span className="wiv2-file-caret">{isExpanded ? "▾" : "▸"}</span>
              <span className="wiv2-file-name">{node.name}</span>
            </button>
            {isExpanded && renderNodes(node.children, depth + 1)}
          </div>
        );
      }
      const status = node.changeStatus ?? "none";
      return (
        <button
          key={`f:${node.path}`}
          type="button"
          className={`wiv2-file-row wiv2-file-leaf${
            selectedPath === node.path ? " wiv2-file-selected" : ""
          }`}
          style={indent}
          onClick={() => selectFile(node.path)}
        >
          <span className="wiv2-file-name">{node.name}</span>
          {status !== "none" && (
            <span className={`wiv2-file-badge wiv2-file-badge-${status}`}>
              {STATUS_BADGE[status]}
            </span>
          )}
        </button>
      );
    });

  return (
    <div className="wiv2-files">
      <div className="wiv2-files-tree">
        <div className="wiv2-files-toolbar">
          <div className="wiv2-toggle-group" role="group" aria-label="File scope">
            <button
              type="button"
              className={`wiv2-toggle${!changesOnly ? " wiv2-toggle-active" : ""}`}
              onClick={() => selectScope(false)}
              aria-pressed={!changesOnly}
            >
              All files
            </button>
            <button
              type="button"
              className={`wiv2-toggle${changesOnly ? " wiv2-toggle-active" : ""}`}
              onClick={() => selectScope(true)}
              aria-pressed={changesOnly}
            >
              Changes{changedCount > 0 ? ` (${changedCount})` : ""}
            </button>
          </div>
        </div>
        <div className="wiv2-files-list">
          {loading && <div className="wiv2-empty">Loading files…</div>}
          {error && <div className="preview-message preview-error">{error}</div>}
          {!loading && !error && visibleFiles.length === 0 && (
            <div className="wiv2-empty">
              {changesOnly ? "No files differ from the base branch." : "No files in this worktree."}
            </div>
          )}
          {!loading && !error && renderNodes(tree, 0)}
        </div>
      </div>

      <div className="wiv2-files-viewer">
        <div className="wiv2-files-toolbar">
          <span className="wiv2-files-viewer-path">{selectedPath ?? "No file selected"}</span>
          {selectedPath && (
            <div className="wiv2-toggle-group" role="group" aria-label="Viewer mode">
              <button
                type="button"
                className={`wiv2-toggle${!showDiff ? " wiv2-toggle-active" : ""}`}
                onClick={() => setShowDiff(false)}
                aria-pressed={!showDiff}
              >
                Code
              </button>
              <button
                type="button"
                className={`wiv2-toggle${showDiff ? " wiv2-toggle-active" : ""}`}
                onClick={() => setShowDiff(true)}
                aria-pressed={showDiff}
              >
                Diff
              </button>
            </div>
          )}
        </div>
        <div className="wiv2-files-content">
          <FileViewer
            selectedPath={selectedPath}
            content={content}
            loading={contentLoading}
            error={contentError}
            showDiff={showDiff}
          />
        </div>
      </div>
    </div>
  );
}

function FileViewer({
  selectedPath,
  content,
  loading,
  error,
  showDiff,
}: {
  selectedPath: string | null;
  content: WorktreeFileContent | null;
  loading: boolean;
  error: string | null;
  showDiff: boolean;
}) {
  if (!selectedPath) {
    return <div className="wiv2-empty">Select a file to view its contents.</div>;
  }
  if (loading) {
    return <div className="wiv2-empty">Loading…</div>;
  }
  if (error) {
    return <div className="preview-message preview-error">{error}</div>;
  }
  if (!content) {
    return <div className="wiv2-empty">No content.</div>;
  }

  if (showDiff) {
    if (!content.diff) {
      return <div className="wiv2-empty">No changes in this file.</div>;
    }
    return <DiffView diff={content.diff} />;
  }

  if (content.isBinary) {
    return <div className="wiv2-empty">Binary file — preview not available.</div>;
  }
  if (content.content === null) {
    return <div className="wiv2-empty">This file has no content to display.</div>;
  }
  return <CodeView code={content.content} />;
}

function CodeView({ code }: { code: string }) {
  // Drop a single trailing newline so a file's final blank line doesn't render
  // as a spurious empty numbered row.
  const lines = code.replace(/\n$/, "").split("\n");
  return (
    <pre className="wiv2-code">
      {lines.map((line, i) => (
        <div key={i} className="wiv2-code-line">
          <span className="wiv2-code-gutter">{i + 1}</span>
          <span className="wiv2-code-text">{line}</span>
        </div>
      ))}
    </pre>
  );
}

function DiffView({ diff }: { diff: string }) {
  const lines = diff.replace(/\n$/, "").split("\n");
  return (
    <pre className="wiv2-diff">
      {lines.map((line, i) => (
        <div key={i} className={`wiv2-diff-line ${diffLineClass(line)}`}>
          {line.length === 0 ? " " : line}
        </div>
      ))}
    </pre>
  );
}

function diffLineClass(line: string): string {
  if (line.startsWith("@@")) return "wiv2-diff-hunk";
  if (line.startsWith("+")) return "wiv2-diff-add";
  if (line.startsWith("-")) return "wiv2-diff-del";
  return "wiv2-diff-ctx";
}
