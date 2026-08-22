import { ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";
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
 * What the viewer draws. "Preview" is offered only for files that have one, so
 * the mode is not free to be anything at any time — see {@link resolveViewMode}.
 */
type ViewMode = "code" | "diff" | "preview";

const VIEW_MODE_LABEL: Record<ViewMode, string> = {
  code: "Code",
  diff: "Diff",
  preview: "Preview",
};

/** What "Preview" draws for a file — one renderer per kind. */
type PreviewKind = "markdown" | "svg";

/**
 * How a file previews, or null when it has nothing to preview as. The one
 * answer all three of its users share — the toolbar's offer, the fallback when
 * the selection moves off a previewable file, and {@link PREVIEW_RENDERER}.
 */
function previewKindOf(path: string): PreviewKind | null {
  const lower = path.toLowerCase();
  if (lower.endsWith(".md") || lower.endsWith(".markdown")) return "markdown";
  if (lower.endsWith(".svg")) return "svg";
  return null;
}

/**
 * What each kind draws. Keyed by the kind rather than picked out by a chain of
 * ifs, so the two halves of adding a previewable suffix stay tied together: a
 * kind named above with nothing to draw it does not compile, where a chain
 * would silently hand the new file to whichever renderer sat at the end of it.
 */
const PREVIEW_RENDERER: Record<PreviewKind, (content: string, path: string) => ReactNode> = {
  markdown: (content) => (
    <div className="wiv2-file-markdown">
      <MarkdownRenderer content={content} />
    </div>
  ),
  svg: (content, path) => <SvgView svg={content} path={path} />,
};

/**
 * Whether the open file can be edited in place. Only text the server actually
 * handed over qualifies, and only under the code view: a binary, an inlined
 * image and a file deleted on the branch all arrive with no `content`, so the
 * one test covers every case the viewer already draws something else for.
 */
function isEditable(content: WorktreeFileContent | null, mode: ViewMode): boolean {
  return mode === "code" && content !== null && !content.isBinary && content.content !== null;
}

/** The modes the toolbar offers for a file: Preview joins them where one exists. */
function offeredViewModes(path: string): ViewMode[] {
  return previewKindOf(path) ? ["code", "diff", "preview"] : ["code", "diff"];
}

/**
 * The mode the viewer actually renders in. The chosen mode is kept as the user
 * clicks through files — a reviewer reading diffs wants the next file's diff
 * too — but "Preview" only exists for some of them, so selecting a file without
 * one falls back to Code without discarding the choice: the next previewable
 * file previews again.
 */
function resolveViewMode(mode: ViewMode, path: string | null): ViewMode {
  if (mode === "preview" && !(path && previewKindOf(path))) return "code";
  return mode;
}

/**
 * Files tab: a Visual-Studio-style explorer with a file tree on the left and a
 * read-only viewer on the right. The tree toggles between every file ("All")
 * and only files that differ from the base branch ("Changes", PR style); the
 * viewer toggles between the full file ("Code"), its unified diff ("Diff") and,
 * for markdown and SVG, the rendered result ("Preview").
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
  // The text being edited, or null when the viewer is read-only. Holding the
  // draft rather than an `editing` flag keeps "what the user typed" and "are we
  // editing" from ever disagreeing, and makes Cancel a single discard.
  const [draft, setDraft] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

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
    if (isNewItem) {
      setDraft(null);
      setSaveError(null);
      return;
    }
    // An open editor holds text that exists nowhere else yet, so the silent
    // re-pull that keeps the viewer current is exactly what would destroy it.
    // The tree still refreshes above; only the open file is left alone.
    if (selectedPathRef.current && draft === null) {
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
      setDraft(null);
      setSaveError(null);
      void loadContent(path, true);
    },
    [loadContent],
  );

  const save = useCallback(async () => {
    if (draft === null || !selectedPath) return;
    setSaving(true);
    setSaveError(null);
    try {
      // The save answers with the file as it now stands, so the viewer's status
      // and diff come from the write itself; the list is re-pulled alongside it
      // for the tree badge the same write may have just changed.
      setContent(await workItemService.saveFileContent(workItem.id, selectedPath, draft));
      setDraft(null);
      void refresh(false);
    } catch (e) {
      setSaveError((e as { message?: string })?.message ?? "Failed to save file.");
    } finally {
      setSaving(false);
    }
  }, [draft, selectedPath, workItem.id, refresh]);

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
  const editable = isEditable(content, mode) && !contentLoading && !contentError;

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
          {/* The mode buttons lock while the editor is open: leaving the code
              view mid-edit would hide the draft behind a pane that cannot show
              it, so the user finishes or discards first. */}
          {selectedPath && (
            <div className="wiv2-toggle-group" role="group" aria-label="Viewer mode">
              {offeredViewModes(selectedPath).map((option) => (
                <button
                  key={option}
                  type="button"
                  className={`wiv2-toggle${mode === option ? " wiv2-toggle-active" : ""}`}
                  onClick={() => setViewMode(option)}
                  aria-pressed={mode === option}
                  disabled={draft !== null}
                >
                  {VIEW_MODE_LABEL[option]}
                </button>
              ))}
            </div>
          )}
          {editable &&
            (draft === null ? (
              <button
                type="button"
                className="wiv2-files-edit"
                onClick={() => setDraft(content?.content ?? "")}
              >
                Edit
              </button>
            ) : (
              <>
                <button
                  type="button"
                  className="wiv2-files-edit wiv2-files-edit-primary"
                  onClick={() => void save()}
                  disabled={saving}
                >
                  {saving ? "Saving…" : "Save"}
                </button>
                <button
                  type="button"
                  className="wiv2-files-edit"
                  onClick={() => {
                    setDraft(null);
                    setSaveError(null);
                  }}
                  disabled={saving}
                >
                  Cancel
                </button>
              </>
            ))}
        </div>
        {saveError && <div className="preview-message preview-error">{saveError}</div>}
        <div className="wiv2-files-content">
          <FileViewer
            selectedPath={selectedPath}
            content={content}
            loading={contentLoading}
            error={contentError}
            mode={mode}
            draft={draft}
            onDraftChange={setDraft}
            saving={saving}
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
  draft,
  onDraftChange,
  saving,
}: {
  selectedPath: string | null;
  content: WorktreeFileContent | null;
  loading: boolean;
  error: string | null;
  mode: ViewMode;
  draft: string | null;
  onDraftChange: (next: string) => void;
  saving: boolean;
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
  const previewKind = mode === "preview" ? previewKindOf(content.path) : null;
  if (previewKind && content.content !== null) {
    return PREVIEW_RENDERER[previewKind](content.content, content.path);
  }

  if (content.content === null) {
    return <div className="wiv2-empty">This file has no content to display.</div>;
  }
  if (draft !== null) {
    return (
      <CodeEditor value={draft} onChange={onDraftChange} path={content.path} readOnly={saving} />
    );
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
 * An SVG drawn as a picture rather than as markup. Worktree SVG is untrusted —
 * the server hands it back as text and never as an inline image, so the markup
 * arrives here unexecuted — and an `<img>` keeps it that way: a document loaded
 * through one is script-inert, where an inline `<svg>` element or
 * `dangerouslySetInnerHTML` would run whatever the file happened to carry. The
 * text is percent-encoded into the data URL rather than base64'd, so non-ASCII
 * markup survives (`btoa` throws on it), with a charset saying what bytes those
 * escapes stand for.
 *
 * Neither way this can end up drawing nothing is left silent: an empty file
 * says so instead of showing a broken image, and markup the browser will not
 * parse says so instead of leaving a blank pane under a toolbar claiming
 * Preview.
 */
function SvgView({ svg, path }: { svg: string; path: string }) {
  const src = useMemo(() => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`, [svg]);
  // What the browser refused, rather than that it refused: a background refresh
  // swaps corrected bytes in under a viewer that stays mounted, and comparing
  // against the current markup draws the replacement on that same render — a
  // flag cleared after the fact would show the stale failure for a frame first.
  const [failedSrc, setFailedSrc] = useState<string | null>(null);

  if (svg.trim() === "") {
    return <div className="wiv2-empty">This file is empty — there is nothing to draw.</div>;
  }
  if (failedSrc === src) {
    return <div className="wiv2-empty">This file could not be drawn as an image.</div>;
  }
  return (
    <div className="wiv2-file-image wiv2-file-svg">
      <img
        src={src}
        alt={`Rendered contents of SVG file ${path}`}
        onError={() => setFailedSrc(src)}
      />
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
 * The file open for editing: one plain textarea over the whole pane, deliberately
 * without the code view's gutter and colouring. Numbering a box whose text moves
 * as it is typed would have to be kept in step with it on every keystroke, and
 * the alignment is wrong the moment a line wraps — the edit is the point here,
 * not the reading.
 */
function CodeEditor({
  value,
  onChange,
  path,
  readOnly,
}: {
  value: string;
  onChange: (next: string) => void;
  path: string;
  readOnly: boolean;
}) {
  return (
    <textarea
      className="wiv2-code-editor"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      readOnly={readOnly}
      spellCheck={false}
      aria-label={`Contents of ${path}`}
    />
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
