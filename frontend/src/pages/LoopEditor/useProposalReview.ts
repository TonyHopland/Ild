import { useCallback, useEffect, useRef, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router";
import { useJoinThenRefresh } from "../../hooks/useJoinThenRefresh";
import { useLatest } from "../../hooks/useLatest";
import type { SignalRConnection } from "../../hooks/useSignalR";
import { loopTemplateProposalService } from "../../services/auth";
import type { LoopTemplate, LoopTemplateProposal } from "../../types";
import type { TypedSignalRMessage } from "../../types/signalr";
import { loadErrorMessage } from "./utils/loadErrorMessage";

/** A Loop Template Proposal open in the editor as its unsaved working copy, on its template. */
interface ProposalReview {
  templateId: string;
  proposal: LoopTemplateProposal;
}

/** The proposal under review when a save was asked for; null when none was. */
export interface SavedFrom {
  proposalId: string | null;
}

/** What a review does to the editor's canvas. Read when it is used, never during render. */
interface ReviewEditor {
  /** The template list as the editor last read it. */
  currentTemplates: () => LoopTemplate[];
  /** Reloads the template list, and resolves to it as it is now. */
  loadTemplates: () => Promise<LoopTemplate[]>;
  /** Opens the saved version of `template` on the canvas. */
  showTemplate: (template: LoopTemplate) => void;
  /** Replaces the open loop with a document as unsaved state; null when it could not. */
  loadDocument: (rawDocument: string, rejectedAs: string) => LoopTemplate | null;
  isCanvasDirty: () => boolean;
  /** Takes `loaded`, as the canvas now holds it, as the canvas without unsaved changes. */
  markClean: (loaded: LoopTemplate) => void;
  setErrorText: (text: string) => void;
}

interface ProposalReviewOptions {
  /** The page's work-item hub connection; the review joins and listens on it. */
  board: SignalRConnection;
  /**
   * The route's template has been opened on the canvas for this route. A
   * template still selected from before the route named it does not count: the
   * route opens it again, which would end a review asked for before that.
   */
  routeTemplateOpen: boolean;
  selectedTemplate: LoopTemplate | null;
  readOnlyVersion: number | null;
  editor: ReviewEditor;
}

/**
 * Reviewing a Loop Template Proposal (ADR-0028). A Review link opens
 * `/loop-editor/<template>?proposal=<id>`. The proposal's document becomes the
 * unsaved working copy of that template, which the chat's loop tools go on
 * editing like any other, and Save names the proposal.
 */
export function useProposalReview({
  board,
  routeTemplateOpen,
  selectedTemplate,
  readOnlyVersion,
  editor,
}: ProposalReviewOptions) {
  const navigate = useNavigate();
  const { templateId: routeTemplateId } = useParams<{ templateId?: string }>();
  const [searchParams] = useSearchParams();
  // The `?proposal=` of the URL.
  const requestedProposalId = searchParams.get("proposal");

  // The proposal open as the working copy, and one asked for over unsaved
  // changes awaiting a confirm. Every reply about one is checked against these
  // before it lands.
  const [review, setReview] = useState<ProposalReview | null>(null);
  const [replaceConfirm, setReplaceConfirm] = useState<ProposalReview | null>(null);
  const reviewRef = useLatest(review);
  const selectedTemplateRef = useLatest(selectedTemplate);
  const editorRef = useLatest(editor);
  // Bumped per `?proposal=` request and whenever the review is closed, so only
  // the latest request's answer lands; and per re-read of the reviewed proposal.
  const requestRef = useRef(0);
  const refreshRef = useRef(0);

  const selectedTemplateId = selectedTemplate?.id ?? null;
  const shownReview = review !== null && review.templateId === selectedTemplateId ? review : null;

  // Given `savedFrom`, only the review of the proposal a save was made from ends.
  const closeReview = useCallback(
    (savedFrom?: SavedFrom) => {
      if (savedFrom && reviewRef.current?.proposal.id !== savedFrom.proposalId) return;
      requestRef.current++;
      setReview(null);
      setReplaceConfirm(null);
    },
    [reviewRef],
  );

  const openReview = useCallback(
    (next: ProposalReview) => {
      if (selectedTemplateRef.current?.id !== next.templateId) return;
      const { loadDocument, markClean } = editorRef.current;
      const loaded = loadDocument(next.proposal.document ?? "", "The proposal could not be opened");
      if (!loaded) return;
      markClean(loaded);
      setReview(next);
    },
    [selectedTemplateRef, editorRef],
  );

  // Re-reads the reviewed proposal, or `proposalId` when its review is only just
  // being opened, for its status; the copy on the canvas is left as it is.
  const refreshReview = useCallback(
    (proposalId = reviewRef.current?.proposal.id) => {
      if (!proposalId) return;
      const refresh = ++refreshRef.current;
      void (async () => {
        try {
          const proposal = await loopTemplateProposalService.get(proposalId);
          if (refresh !== refreshRef.current) return;
          setReview((current) =>
            current?.proposal.id === proposalId ? { ...current, proposal } : current,
          );
        } catch (err) {
          console.error(err);
        }
      })();
    },
    [reviewRef],
  );

  // Back to the saved version as it is now, which may be newer than the one the
  // review was opened on.
  const discardReview = useCallback(async () => {
    const current = reviewRef.current;
    if (!current) return;
    const loaded = await editorRef.current.loadTemplates();
    if (reviewRef.current?.proposal.id !== current.proposal.id) return;
    const saved = loaded.find((template) => template.id === current.templateId);
    if (saved) editorRef.current.showTemplate(saved);
    else closeReview();
  }, [reviewRef, editorRef, closeReview]);

  // A proposal saved from the template list is a new version of that template:
  // show it, unless the canvas holds work of its own, which is then told it is
  // behind rather than replaced.
  const handleListedProposalDecided = useCallback(
    async (templateId: string) => {
      const known = editorRef.current
        .currentTemplates()
        .find((template) => template.id === templateId)?.version;
      const loaded = await editorRef.current.loadTemplates();
      const open = selectedTemplateRef.current;
      const latest = loaded.find((template) => template.id === templateId);
      if (!open || open.id !== templateId || !latest || latest.version === known) return;
      if (!reviewRef.current && !editorRef.current.isCanvasDirty())
        editorRef.current.showTemplate(latest);
      else
        editorRef.current.setErrorText(
          `Version ${latest.version} of this template was just saved from a proposal. The loop open here does not include it, and saving it replaces version ${latest.version}.`,
        );
    },
    [editorRef, selectedTemplateRef, reviewRef],
  );

  // The request in the URL is taken once the route's template is open, and then
  // dropped from the URL, so following the same link again asks again.
  useEffect(() => {
    if (!requestedProposalId || !routeTemplateId) return;
    // An old version open read-only is left for the latest, which a review edits.
    if (readOnlyVersion !== null) {
      const latest = editorRef.current
        .currentTemplates()
        .find((template) => template.id === routeTemplateId);
      if (latest) editorRef.current.showTemplate(latest);
      return;
    }
    if (!routeTemplateOpen || selectedTemplateId !== routeTemplateId) return;
    const templateId = routeTemplateId;
    const request = ++requestRef.current;
    void navigate(`/loop-editor/${templateId}`, { replace: true });
    void (async () => {
      let proposal: LoopTemplateProposal;
      try {
        proposal = await loopTemplateProposalService.get(requestedProposalId);
      } catch (error) {
        if (request === requestRef.current)
          editorRef.current.setErrorText(loadErrorMessage(error, "Failed to load the proposal."));
        return;
      }
      if (request !== requestRef.current) return;
      if (proposal.templateId !== templateId || proposal.status !== "Pending") {
        editorRef.current.setErrorText(
          `This proposal is ${proposal.status} and can no longer be reviewed on this template.`,
        );
        return;
      }
      if (editorRef.current.isCanvasDirty()) setReplaceConfirm({ templateId, proposal });
      else openReview({ templateId, proposal });
    })();
  }, [
    requestedProposalId,
    routeTemplateId,
    routeTemplateOpen,
    selectedTemplateId,
    readOnlyVersion,
    editorRef,
    navigate,
    openReview,
  ]);

  const confirmReplace = useCallback(() => {
    const pending = replaceConfirm;
    setReplaceConfirm(null);
    if (!pending) return;
    openReview(pending);
    // It may have been decided while the question was open.
    refreshReview(pending.proposal.id);
  }, [replaceConfirm, openReview, refreshReview]);

  const cancelReplace = useCallback(() => setReplaceConfirm(null), []);

  // The board's hub says when a template's proposals change, the reviewed one
  // going stale included. Joined on every (re)connect, and the reviewed proposal
  // re-read then, since a hint sent while disconnected is lost.
  const { on: onBoard, off: offBoard } = board;
  useJoinThenRefresh(board, null, refreshReview);

  useEffect(() => {
    const onChanged = (msg: TypedSignalRMessage<"LoopTemplateProposalsChanged">) => {
      if (msg.payload.templateId === reviewRef.current?.templateId) refreshReview();
    };
    onBoard("LoopTemplateProposalsChanged", onChanged);
    return () => offBoard("LoopTemplateProposalsChanged", onChanged);
  }, [onBoard, offBoard, reviewRef, refreshReview]);

  return {
    /** The review, while the template it is on is the one open. */
    shownReview,
    /** The proposal a save of the canvas is made from: the one under review. */
    saveFromProposalId: shownReview?.proposal.id ?? null,
    closeReview,
    refreshReview,
    discardReview,
    handleListedProposalDecided,
    /** A proposal waits for the human to confirm it may replace unsaved changes. */
    confirmingReplace: replaceConfirm !== null,
    confirmReplace,
    cancelReplace,
  };
}
