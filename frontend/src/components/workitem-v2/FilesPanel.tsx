import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  WorkItem,
  WorktreeFileChangeStatus,
  WorktreeFileContent,
  WorktreeFileEntry,
} from "../../types";
import { workItemService } from "../../services/auth";
import { buildFileTree, FileTreeNode } from "../../utils/fileTree";
import { parseUnifiedDiff } from "../../utils/unifiedDiff";
import { highlightLines } from "../../utils/syntaxHighlight";
import MarkdownRenderer from "../MarkdownRenderer";

const STATUS_BADGE: Record<Exclude<WorktreeFileChangeStatus, "none">, string> = {
  added: "A",
  modified: "M",
  deleted: "D",
};

/**
 * What the viewer draws. "Preview" is offered for markdown only, so the mode is
 * not free to be anything at any time — see {@link resolveViewMode}.
 */
type ViewMode = "code" | "diff" | "preview";

const VIEW_MODE_LABEL: Record<ViewMode, string> = {
  code: "Code",
  diff: "Diff",
  preview: "Preview",
};

function isMarkdownPath(path: string): boolean {
  const lower = path.toLowerCase();
  return lower.endsWith(".md") || lower.endsWith(".markdown");
}

/** The modes the toolbar offers for a file: Preview joins them for markdown. */
function offeredViewModes(path: string): ViewMode[] {
  return isMarkdownPath(path) ? ["code", "diff", "preview"] : ["code", "diff"];
}

/**
 * The mode the viewer actually renders in. The chosen mode is kept as the user
 * clicks through files — a reviewer reading diffs wants the next file's diff
 * too — but "Preview" only exists for markdown, so selecting a non-markdown file
 * falls back to Code without discarding the choice: the next markdown file
 * previews again.
 */
function resolveViewMode(mode: ViewMode, path: string | null): ViewMode {
  if (mode === "preview" && !(path && isMarkdownPath(path))) return "code";
  return mode;
}

/**
 * Files tab: a Visual-Studio-style explorer with a file tree on the left and a
 * read-only viewer on the right. The tree toggles between every file ("All")
 * and only files that differ from the base branch ("Changes", PR style); the
 * viewer toggles between the full file ("Code"), its unified diff ("Diff") and,
 * for markdown, the rendered document ("Preview").
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
  const [viewMode, setViewMode] = useState<ViewMode>("code");

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

  const mode = resolveViewMode(viewMode, selectedPath);

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
              {offeredViewModes(selectedPath).map((option) => (
                <button
                  key={option}
                  type="button"
                  className={`wiv2-toggle${mode === option ? " wiv2-toggle-active" : ""}`}
                  onClick={() => setViewMode(option)}
                  aria-pressed={mode === option}
                >
                  {VIEW_MODE_LABEL[option]}
                </button>
              ))}
            </div>
          )}
        </div>
        <div className="wiv2-files-content">
          <FileViewer
            selectedPath={selectedPath}
            content={content}
            loading={contentLoading}
            error={contentError}
            mode={mode}
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
  mode,
}: {
  selectedPath: string | null;
  content: WorktreeFileContent | null;
  loading: boolean;
  error: string | null;
  mode: ViewMode;
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

  if (mode === "diff") {
    if (!content.diff) {
      return <div className="wiv2-empty">No changes in this file.</div>;
    }
    return <DiffView diff={content.diff} />;
  }

  if (content.imageMimeType && content.imageBase64) {
    return (
      <ImageView
        mimeType={content.imageMimeType}
        base64={content.imageBase64}
        path={content.path}
      />
    );
  }
  if (content.isBinary) {
    return <div className="wiv2-empty">Binary file — preview not available.</div>;
  }

  // Preview ranks below the guards above rather than over them: a file the
  // server could not hand over as text has nothing to render as a document,
  // whatever its name ends in. An empty markdown file does — it previews as an
  // empty document rather than falling through to the code view, which would
  // leave the toolbar claiming Preview over a pane showing something else.
  if (mode === "preview" && content.content !== null) {
    return (
      <div className="wiv2-file-markdown">
        <MarkdownRenderer content={content.content} />
      </div>
    );
  }

  if (content.content === null) {
    return <div className="wiv2-empty">This file has no content to display.</div>;
  }
  return <CodeView code={content.content} path={content.path} />;
}

/**
 * An image file drawn from the bytes the content response already carried, as a
 * data URL — the file endpoint is bearer-authenticated, so a `src` pointing back
 * at it would be fetched by the browser without the token.
 */
function ImageView({ mimeType, base64, path }: { mimeType: string; base64: string; path: string }) {
  return (
    <div className="wiv2-file-image">
      <img src={`data:${mimeType};base64,${base64}`} alt={`Contents of image file ${path}`} />
    </div>
  );
}

/**
 * The file itself, numbered and syntax-coloured. {@link highlightLines} decides
 * the language from the path and hands back one token list per line — a single
 * unclassified token for a file it has no grammar for, so an unknown extension
 * renders exactly as it always has, as bare text inside the line box.
 */
function CodeView({ code, path }: { code: string; path: string }) {
  const lines = useMemo(() => highlightLines(code, path), [code, path]);
  return (
    <pre className="wiv2-code">
      {lines.map((tokens, i) => (
        <div key={i} className="wiv2-code-line">
          <span className="wiv2-code-gutter">{i + 1}</span>
          <span className="wiv2-code-text">
            {tokens.map((token, j) =>
              token.className ? (
                <span key={j} className={token.className}>
                  {token.text}
                </span>
              ) : (
                token.text
              ),
            )}
          </span>
        </div>
      ))}
    </pre>
  );
}

/**
 * The server's unified diff for one file, shaded in two tiers: a changed line
 * lightly, so the changed region stays visible in context, and the words that
 * actually differ from its counterpart strongly, wherever
 * {@link parseUnifiedDiff} could pair a removed line with the added one that
 * replaced it. Same two-tier treatment the Loop Editor's save-time review gives
 * its own diff, over a patch git produced rather than one computed here.
 */
function DiffView({ diff }: { diff: string }) {
  const rows = useMemo(() => parseUnifiedDiff(diff), [diff]);
  return (
    <pre className="wiv2-diff">
      {rows.map((row, i) => (
        <div key={i} className={`wiv2-diff-line wiv2-diff-${row.kind}`}>
          {row.segments ? (
            <>
              {/* The marker stays outside the segments so it never reads as a
                  changed word, and the line still copies as raw patch text. */}
              {row.text.slice(0, 1)}
              {row.segments.map((segment, segIdx) => (
                <span
                  key={segIdx}
                  className={segment.changed ? `wiv2-diff-seg-${row.kind}` : undefined}
                >
                  {segment.text}
                </span>
              ))}
            </>
          ) : row.text.length === 0 ? (
            " "
          ) : (
            row.text
          )}
        </div>
      ))}
    </pre>
  );
}
