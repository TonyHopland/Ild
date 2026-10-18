import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type Dispatch,
  type RefObject,
  type SetStateAction,
} from "react";
import { useNavigate, useParams, useSearchParams } from "react-router";
import { useJoinThenRefresh } from "../../hooks/useJoinThenRefresh";
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

/**
 * The proposal review open in the editor, and ending it: {@link useReviewSession}
 * sets it up before anything that replaces the canvas, since every replacement
 * ends it, and {@link useProposalReview} does the rest.
 */
export interface ReviewSession {
  review: ProposalReview | null;
  replaceConfirm: ProposalReview | null;
  /** Ends the open review; given `savedFrom`, only if it is the review that save was made from. */
  closeReview: (savedFrom?: SavedFrom) => void;
  setReview: Dispatch<SetStateAction<ProposalReview | null>>;
  setReplaceConfirm: Dispatch<SetStateAction<ProposalReview | null>>;
  reviewRef: RefObject<ProposalReview | null>;
  /** Bumped whenever the canvas is replaced, so an answer to a review asked for before that is dropped. */
  reviewRequestRef: RefObject<number>;
  selectedTemplateId: string | null;
  selectedTemplateRef: RefObject<LoopTemplate | null>;
}

/** The review state of {@link ReviewSession}, for the template open in the editor. */
export function useReviewSession(selectedTemplate: LoopTemplate | null): ReviewSession {
  // The proposal open as the working copy, and one asked for over unsaved
  // changes awaiting a confirm. Every reply about one is checked against these
  // before it lands.
  const [review, setReview] = useState<ProposalReview | null>(null);
  const [replaceConfirm, setReplaceConfirm] = useState<ProposalReview | null>(null);
  const reviewRef = useRef<ProposalReview | null>(null);
  reviewRef.current = review;
  const reviewRequestRef = useRef(0);
  const selectedTemplateRef = useRef(selectedTemplate);
  selectedTemplateRef.current = selectedTemplate;

  const closeReview = useCallback((savedFrom?: SavedFrom) => {
    if (savedFrom && reviewRef.current?.proposal.id !== savedFrom.proposalId) return;
    reviewRequestRef.current++;
    setReview(null);
    setReplaceConfirm(null);
  }, []);

  return {
    review,
    replaceConfirm,
    closeReview,
    setReview,
    setReplaceConfirm,
    reviewRef,
    reviewRequestRef,
    selectedTemplateId: selectedTemplate?.id ?? null,
    selectedTemplateRef,
  };
}

interface ProposalReviewOptions {
  /** The page's work-item hub connection; the review joins and listens on it. */
  board: SignalRConnection;
  readOnlyVersion: number | null;
  templatesRef: RefObject<LoopTemplate[]>;
  loadTemplates: () => Promise<void>;
  selectTemplate: (template: LoopTemplate) => void;
  /** Replaces the open loop with a document as unsaved state; null when it could not. */
  loadDocument: (rawDocument: string, rejectedAs: string) => LoopTemplate | null;
  isCanvasDirty: () => boolean;
  /** Takes `loaded`, as the canvas now holds it, as the canvas without unsaved changes. */
  markClean: (loaded: LoopTemplate) => void;
  setErrorText: (text: string) => void;
}

/**
 * Reviewing a Loop Template Proposal (ADR-0028). A Review link opens
 * `/loop-editor/<template>?proposal=<id>`. The proposal's document becomes the
 * unsaved working copy of that template, which the chat's loop tools go on
 * editing like any other, and Save names the proposal. Called after the effect
 * that opens the route's template, so that its `?proposal=` request follows
 * that effect's canvas replacement when both run in one commit.
 */
export function useProposalReview(
  session: ReviewSession,
  {
    board,
    readOnlyVersion,
    templatesRef,
    loadTemplates,
    selectTemplate,
    loadDocument,
    isCanvasDirty,
    markClean,
    setErrorText,
  }: ProposalReviewOptions,
) {
  const {
    review,
    replaceConfirm,
    closeReview,
    setReview,
    setReplaceConfirm,
    reviewRef,
    reviewRequestRef,
    selectedTemplateId,
    selectedTemplateRef,
  } = session;
  const navigate = useNavigate();
  const { templateId: routeTemplateId } = useParams<{ templateId?: string }>();
  const [searchParams] = useSearchParams();
  // The `?proposal=` of the URL.
  const requestedProposalId = searchParams.get("proposal");
  // Per re-read of the reviewed proposal.
  const reviewRefreshRef = useRef(0);

  const openReview = useCallback(
    (next: ProposalReview) => {
      if (selectedTemplateRef.current?.id !== next.templateId) return;
      const loaded = loadDocument(next.proposal.document ?? "", "The proposal could not be opened");
      if (!loaded) return;
      markClean(loaded);
      setReview(next);
    },
    [selectedTemplateRef, loadDocument, markClean],
  );

  // Re-reads the reviewed proposal, or `proposalId` when its review is only just
  // being opened, for its status; the copy on the canvas is left as it is.
  const refreshReview = useCallback((proposalId = reviewRef.current?.proposal.id) => {
    if (!proposalId) return;
    const refresh = ++reviewRefreshRef.current;
    void (async () => {
      try {
        const proposal = await loopTemplateProposalService.get(proposalId);
        if (refresh !== reviewRefreshRef.current) return;
        setReview((current) =>
          current?.proposal.id === proposalId ? { ...current, proposal } : current,
        );
      } catch (err) {
        console.error(err);
      }
    })();
  }, []);

  // Back to the saved version as it is now, which may be newer than the one the
  // review was opened on.
  const discardReview = useCallback(async () => {
    const current = reviewRef.current;
    if (!current) return;
    await loadTemplates();
    if (reviewRef.current?.proposal.id !== current.proposal.id) return;
    const saved = templatesRef.current.find((template) => template.id === current.templateId);
    if (saved) selectTemplate(saved);
    else closeReview();
  }, [loadTemplates, templatesRef, selectTemplate, closeReview]);

  // A proposal saved from the template list is a new version of that template:
  // show it, unless the canvas holds work of its own, which is then told it is
  // behind rather than replaced.
  const handleListedProposalDecided = useCallback(
    async (templateId: string) => {
      const known = templatesRef.current.find((template) => template.id === templateId)?.version;
      await loadTemplates();
      const open = selectedTemplateRef.current;
      const latest = templatesRef.current.find((template) => template.id === templateId);
      if (!open || open.id !== templateId || !latest || latest.version === known) return;
      if (!reviewRef.current && !isCanvasDirty()) selectTemplate(latest);
      else
        setErrorText(
          `Version ${latest.version} of this template was just saved from a proposal. The loop open here does not include it, and saving it replaces version ${latest.version}.`,
        );
    },
    [templatesRef, selectedTemplateRef, loadTemplates, isCanvasDirty, selectTemplate, setErrorText],
  );

  // The request in the URL is taken once the template it names is open, and then
  // dropped from the URL, so following the same link again asks again.
  useEffect(() => {
    if (!requestedProposalId || !routeTemplateId) return;
    // An old version open read-only is left for the latest, which a review edits.
    if (readOnlyVersion !== null) {
      const latest = templatesRef.current.find((template) => template.id === routeTemplateId);
      if (latest) selectTemplate(latest);
      return;
    }
    if (selectedTemplateId !== routeTemplateId) return;
    const templateId = routeTemplateId;
    const request = ++reviewRequestRef.current;
    void navigate(`/loop-editor/${templateId}`, { replace: true });
    void (async () => {
      let proposal: LoopTemplateProposal;
      try {
        proposal = await loopTemplateProposalService.get(requestedProposalId);
      } catch (error) {
        if (request === reviewRequestRef.current)
          setErrorText(loadErrorMessage(error, "Failed to load the proposal."));
        return;
      }
      if (request !== reviewRequestRef.current) return;
      if (proposal.templateId !== templateId || proposal.status !== "Pending") {
        setErrorText(
          `This proposal is ${proposal.status} and can no longer be reviewed on this template.`,
        );
        return;
      }
      if (isCanvasDirty()) setReplaceConfirm({ templateId, proposal });
      else openReview({ templateId, proposal });
    })();
  }, [
    requestedProposalId,
    routeTemplateId,
    selectedTemplateId,
    readOnlyVersion,
    templatesRef,
    selectTemplate,
    navigate,
    setErrorText,
    isCanvasDirty,
    openReview,
  ]);

  const confirmReplace = () => {
    const pending = replaceConfirm;
    setReplaceConfirm(null);
    if (!pending) return;
    openReview(pending);
    // It may have been decided while the question was open.
    refreshReview(pending.proposal.id);
  };

  const cancelReplace = () => setReplaceConfirm(null);

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
  }, [onBoard, offBoard, refreshReview]);

  return {
    review,
    /** The review, while the template it is on is the one open. */
    shownReview: review !== null && review.templateId === selectedTemplateId ? review : null,
    replaceConfirm,
    refreshReview,
    discardReview,
    handleListedProposalDecided,
    confirmReplace,
    cancelReplace,
  };
}
