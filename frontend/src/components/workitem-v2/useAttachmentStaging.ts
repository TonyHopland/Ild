import { useCallback, useEffect, useRef, useState } from "react";
import { AttachmentLimits } from "../../types";
import { settingsService, workItemService } from "../../services/auth";
import { oversizeMessage, stagedFileName } from "../../utils/attachments";

/** One file waiting to be uploaded, and what has become of it so far. */
export interface StagedAttachment {
  key: string;
  file: File;
  status: "pending" | "uploaded";
  /** The attachment this became on the work item, once it landed. */
  attachmentId: string | null;
  /**
   * The name the work item stores it under, which is not always the browser's:
   * the server trims a name and keeps only its last path segment.
   */
  storedName: string | null;
  error: string | null;
}

/** What an attempt to upload the staged batch left behind. */
export interface UploadOutcome {
  /** Every staged file is on the work item. */
  ok: boolean;
  /**
   * The batch stopped because the dialog moved to another work item. Whatever is
   * staged now belongs to that one, so this attempt has nothing left to say —
   * the view that started it is gone.
   */
  abandoned: boolean;
  /**
   * The names of every file now stored on the item — including ones an earlier
   * attempt already landed, not just the ones this attempt sent.
   */
  storedNames: string[];
  errors: string[];
}

export interface AttachmentStaging {
  staged: StagedAttachment[];
  limits: AttachmentLimits | null;
  stagingError: string | null;
  uploading: boolean;
  add: (files: FileList | File[] | null | undefined) => void;
  remove: (key: string) => void;
  clear: () => void;
  /**
   * Whether anything is still waiting to be uploaded, read from the staging list
   * itself rather than from a render — a caller that has awaited a request since
   * it was clicked would otherwise decide on a list that has moved on.
   */
  hasPending: () => boolean;
  /**
   * Drops the entry that became this attachment, for when it is removed from the
   * work item: the staging rows and the item's own list are two views of the
   * same files and must not disagree while the dialog is open.
   */
  forgetUploaded: (attachmentId: string) => void;
  handlePaste: (event: React.ClipboardEvent) => void;
  uploadAll: (workItemId: string) => Promise<UploadOutcome>;
}

const messageOf = (error: unknown, fallback: string) =>
  (error as { message?: string })?.message ?? fallback;

/**
 * What the files that landed are called. The name the work item recorded is the
 * truth — the server trims it and keeps only its last path segment — and the
 * browser's own name stands in only for an upload that answered without any.
 */
const namesOf = (entries: StagedAttachment[]) =>
  entries
    .filter((entry) => entry.status === "uploaded")
    .map((entry) => entry.storedName ?? entry.file.name);

/**
 * What this instance may accept, read once for the dialog. A failed read leaves
 * the limits unknown and the server the only enforcer.
 */
export function useAttachmentLimits(): AttachmentLimits | null {
  const [limits, setLimits] = useState<AttachmentLimits | null>(null);

  useEffect(() => {
    let cancelled = false;
    settingsService
      .getAttachmentLimits()
      .then((result) => {
        if (!cancelled) setLimits(result);
      })
      .catch(() => {
        // Inventing a limit would refuse files an instance configured higher
        // accepts, so there simply is no client-side gate until this arrives.
      });
    return () => {
      cancelled = true;
    };
  }, []);

  return limits;
}

/**
 * The files a human has picked, dropped or pasted but not yet uploaded, for the
 * form to save. They belong to the work item they were staged on, and each is
 * marked done on its own successful upload, so a save that fails partway can be
 * retried without landing a second copy of what already arrived.
 */
export function useAttachmentStaging(
  workItemId: string | undefined,
  limits: AttachmentLimits | null,
): AttachmentStaging {
  const [staged, setStaged] = useState<StagedAttachment[]>([]);
  const [stagingError, setStagingError] = useState<string | null>(null);
  const [uploading, setUploading] = useState(false);

  // uploadAll walks the list it is looking at while marking entries done, so it
  // reads the staged files through a ref rather than a render's snapshot.
  const stagedRef = useRef<StagedAttachment[]>(staged);
  const nextKey = useRef(0);

  const applyStaged = useCallback((update: (prev: StagedAttachment[]) => StagedAttachment[]) => {
    stagedRef.current = update(stagedRef.current);
    setStaged(stagedRef.current);
  }, []);

  useEffect(() => {
    if (!limits) return;
    // A file staged before the limits arrived was never weighed against them. It
    // is weighed now, while it is still only staged: the work item asks that no
    // request carrying an oversize file is ever sent, and until the limits are
    // here the gate has nothing to gate on.
    const refusals: string[] = [];
    applyStaged((prev) =>
      prev.filter((entry) => {
        if (entry.status === "uploaded" || entry.file.size <= limits.maxBytesPerFile) return true;
        refusals.push(oversizeMessage(entry.file.name, limits.maxBytesPerFile));
        return false;
      }),
    );
    if (refusals.length > 0) setStagingError(refusals.join(" "));
  }, [limits, applyStaged]);

  // Every reset starts a new generation. An upload batch that began under an
  // older one has to stop: the files it would find now were staged for the work
  // item the human has since opened, and its own target id is the previous one.
  const generation = useRef(0);

  useEffect(() => {
    generation.current += 1;
    applyStaged(() => []);
    setStagingError(null);
  }, [workItemId, applyStaged]);

  const add = useCallback(
    (files: FileList | File[] | null | undefined) => {
      const incoming = Array.from(files ?? []);
      if (incoming.length === 0) return;

      const maxBytes = limits?.maxBytesPerFile;
      const now = new Date();
      const accepted: File[] = [];
      const refusals: string[] = [];
      for (const file of incoming) {
        const name = stagedFileName(file, now);
        const named = name === file.name ? file : new File([file], name, { type: file.type });
        if (maxBytes !== undefined && named.size > maxBytes) {
          refusals.push(oversizeMessage(name, maxBytes));
        } else {
          accepted.push(named);
        }
      }

      setStagingError(refusals.length === 0 ? null : refusals.join(" "));
      if (accepted.length === 0) return;
      applyStaged((prev) => [
        ...prev,
        ...accepted.map((file) => ({
          key: `staged-${nextKey.current++}`,
          file,
          status: "pending" as const,
          attachmentId: null,
          storedName: null,
          error: null,
        })),
      ]);
    },
    [limits, applyStaged],
  );

  const remove = useCallback(
    (key: string) => applyStaged((prev) => prev.filter((entry) => entry.key !== key)),
    [applyStaged],
  );

  const clear = useCallback(() => {
    // A batch walking this list has nothing left to walk, and must not go on to
    // report on files it never sent: emptying the list abandons it, exactly as
    // moving to another work item does.
    generation.current += 1;
    applyStaged(() => []);
    setStagingError(null);
  }, [applyStaged]);

  const hasPending = useCallback(
    () => stagedRef.current.some((entry) => entry.status === "pending"),
    [],
  );

  const forgetUploaded = useCallback(
    (attachmentId: string) =>
      applyStaged((prev) => prev.filter((entry) => entry.attachmentId !== attachmentId)),
    [applyStaged],
  );

  // Wired to the form, not to the picker: the textareas a screenshot is pasted
  // into are siblings of it, so a handler inside the picker would never see the
  // event. A paste carrying no file is left alone.
  const handlePaste = useCallback(
    (event: React.ClipboardEvent) => {
      const files = Array.from(event.clipboardData?.files ?? []);
      if (files.length === 0) return;
      event.preventDefault();
      add(files);
    },
    [add],
  );

  const drain = useCallback(
    async (targetWorkItemId: string): Promise<UploadOutcome> => {
      setUploading(true);
      // A file dropped while the batch is in flight belongs to this save too, so
      // the walk takes the list as it stands rather than the snapshot it started
      // with. Each entry is attempted once, which is what ends the walk.
      const attempted = new Set<string>();
      const batch = generation.current;
      try {
        for (;;) {
          if (generation.current !== batch) break;
          const entry = stagedRef.current.find(
            (candidate) => candidate.status === "pending" && !attempted.has(candidate.key),
          );
          if (!entry) break;
          attempted.add(entry.key);
          try {
            const created = await workItemService.uploadAttachment(targetWorkItemId, entry.file);
            // One file per request, so the response describes this one: its id is
            // the handle everything else refers to it by, and its name is what
            // the work item actually stores, which the server may have trimmed.
            const stored = created?.[0];
            applyStaged((prev) =>
              prev.map((s) =>
                s.key === entry.key
                  ? {
                      ...s,
                      status: "uploaded" as const,
                      attachmentId: stored?.id ?? null,
                      storedName: stored?.fileName ?? null,
                      error: null,
                    }
                  : s,
              ),
            );
          } catch (error) {
            // One file refused — most likely on the per-item total, which only
            // the server can compute — must not hold up the rest of the batch.
            const message = messageOf(error, `Failed to upload ${entry.file.name}.`);
            applyStaged((prev) =>
              prev.map((s) => (s.key === entry.key ? { ...s, error: message } : s)),
            );
          }
        }
      } finally {
        setUploading(false);
      }

      if (generation.current !== batch) {
        return { ok: false, abandoned: true, storedNames: [], errors: [] };
      }

      const current = stagedRef.current;
      return {
        ok: current.every((entry) => entry.status === "uploaded"),
        abandoned: false,
        storedNames: namesOf(current),
        errors: current.map((entry) => entry.error).filter((e): e is string => !!e),
      };
    },
    [applyStaged],
  );

  // One batch at a time walks the staging list: two walks would each pick up the
  // same pending file and store it twice. A caller whose work the running batch
  // is already doing — a second save of the same list for the same item — joins
  // it and takes its outcome. A caller asking for anything else waits for it and
  // then walks itself, because the batch that is running belongs to an item this
  // caller is not saving, and its outcome says nothing about these files.
  const inFlight = useRef<{
    outcome: Promise<UploadOutcome>;
    targetWorkItemId: string;
    generation: number;
  } | null>(null);

  const uploadAll = useCallback(
    (targetWorkItemId: string): Promise<UploadOutcome> => {
      const running = inFlight.current;
      if (
        running &&
        running.targetWorkItemId === targetWorkItemId &&
        running.generation === generation.current
      ) {
        return running.outcome;
      }

      const outcome = (async () => {
        // Queued rather than concurrent: whatever is walking the list finishes
        // before this one starts.
        if (running) await running.outcome.catch(() => {});
        return drain(targetWorkItemId);
      })().finally(() => {
        if (inFlight.current?.outcome === outcome) inFlight.current = null;
      });

      inFlight.current = { outcome, targetWorkItemId, generation: generation.current };
      return outcome;
    },
    [drain],
  );

  return {
    staged,
    limits,
    stagingError,
    uploading,
    add,
    remove,
    clear,
    hasPending,
    forgetUploaded,
    handlePaste,
    uploadAll,
  };
}
