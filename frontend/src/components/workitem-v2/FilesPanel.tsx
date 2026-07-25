import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  WorkItem,
  WorktreeFileChangeStatus,
  WorktreeFileContent,
  WorktreeFileEntry,
} from "../../types";
import { workItemService } from "../../services/auth";
import { buildFileTree, FileTreeNode } from "../../utils/fileTree";
import { computeWordDiff, createWordDiffBudget, DiffSegment } from "../../utils/jsonDiff";

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

/**
 * The server's unified diff for one file, shaded in two tiers: a changed line
 * lightly, so the changed region stays visible in context, and the words that
 * actually differ from its counterpart strongly, whenever the pair was one
 * {@link computeWordDiff} could work out. Same treatment the Loop Editor's
 * save-time review gives its own diff, over a patch git produced rather than one
 * computed here.
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

type DiffRowKind = "hunk" | "add" | "del" | "ctx";

/** One rendered row of a unified diff. */
interface DiffRow {
  /** The raw diff line, marker included — what renders when `segments` is absent. */
  text: string;
  kind: DiffRowKind;
  /**
   * Word-level runs of the line's payload (`text` past its marker), present only
   * on a del/add line {@link parseUnifiedDiff} could pair with a counterpart.
   * They cover the payload in full, so the renderer can shade every run. Absent
   * for an unpaired line or a pair {@link computeWordDiff} declined — both
   * ordinary, and both leave the line to the whole-line treatment.
   */
  segments?: DiffSegment[];
}

/**
 * Classify every line of a unified diff for shading, and pair up the del/add
 * runs within each hunk so the words that differ can be emphasised.
 *
 * Pairing is index-wise: the k-th removed line of a run against the k-th added
 * line of the run following it, up to the shorter of the two. Lines past that
 * have no counterpart to compare against, and borrowing one from a neighbour
 * would emphasise words that were never edited.
 */
function parseUnifiedDiff(diff: string): DiffRow[] {
  const rows: DiffRow[] = diff
    .replace(/\n$/, "")
    .split("\n")
    .map((text) => ({ text, kind: rowKind(text) }));

  // One allowance for the whole file, spent the way the save-diff path spends
  // it: the caps exist to keep this pass off the critical path, and a fresh
  // allowance per pair would let a heavily edited file multiply right through
  // them.
  const budget = createWordDiffBudget();

  // Only lines inside a hunk are content. The patch preamble's "--- a/x" and
  // "+++ b/x" are prefixed like a del/add pair and shade like one (longstanding
  // behaviour, deliberately left alone), but diffing those two paths against
  // each other would emphasise nothing a reader cares about. The discriminator
  // has to be "have we reached an @@ yet" rather than the prefix, because inside
  // a hunk "---" is genuine content: a deleted line reading "-- note" arrives
  // with its marker as "--- note".
  let inHunk = false;
  let i = 0;
  while (i < rows.length) {
    if (rows[i].kind === "hunk") {
      inHunk = true;
      i++;
    } else if (!inHunk || rows[i].kind !== "del") {
      i++;
    } else {
      const dels = collectRun(rows, i, "del");
      const adds = collectRun(rows, dels.end, "add");
      const pairs = Math.min(dels.indices.length, adds.indices.length);
      for (let k = 0; k < pairs; k++) {
        const del = rows[dels.indices[k]];
        const add = rows[adds.indices[k]];
        const words = computeWordDiff(del.text.slice(1), add.text.slice(1), budget);
        if (words) {
          del.segments = words.del;
          add.segments = words.add;
        }
      }
      // The del run is never empty here, so this always advances.
      i = adds.end;
    }
  }

  return rows;
}

/**
 * The consecutive `kind` lines starting at `from`, and where that run ends.
 *
 * A "\ No newline at end of file" marker is stepped over rather than ending the
 * run: it annotates the line before it, and in a file with no trailing newline
 * git puts one squarely between the removed and added sides of the pair most
 * worth segmenting. Inside a hunk a leading backslash can only be that marker —
 * real content always carries a +, - or space marker first.
 */
function collectRun(
  rows: DiffRow[],
  from: number,
  kind: DiffRowKind,
): { indices: number[]; end: number } {
  const indices: number[] = [];
  let i = from;
  while (i < rows.length) {
    if (rows[i].kind === kind) indices.push(i);
    else if (!rows[i].text.startsWith("\\")) break;
    i++;
  }
  return { indices, end: i };
}

function rowKind(line: string): DiffRowKind {
  if (line.startsWith("@@")) return "hunk";
  if (line.startsWith("+")) return "add";
  if (line.startsWith("-")) return "del";
  return "ctx";
}
