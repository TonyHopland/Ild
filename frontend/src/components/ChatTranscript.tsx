import type { ReactNode } from "react";
import EditProposalCard from "./EditProposalCard";
import GlobalVariableProposalCard from "./GlobalVariableProposalCard";
import LoopTemplateProposalCard from "./LoopTemplateProposalCard";
import MarkdownRenderer from "./MarkdownRenderer";
import { placeChatProposals } from "./editProposalPlacement";
import { useEditProposals } from "../hooks/useEditProposals";
import { useGlobalVariableProposals } from "../hooks/useGlobalVariableProposals";
import { useLoopTemplateProposals } from "../hooks/useLoopTemplateProposals";
import type { ChatMessage } from "../types";

const revealCard = (card: HTMLElement) => card.scrollIntoView?.({ block: "center" });

/**
 * One chat's transcript with the edit proposals, global variable suggestions and
 * loop template proposals it made, each after the reply of the turn that made it,
 * then the reply still streaming. Render it keyed by the chat's id.
 *
 * Everything is one keyed list, so a card that moves to a new slot (its reply
 * landing) is moved rather than remounted, and keeps a decision in progress.
 */
export default function ChatTranscript({
  chatSessionId,
  messages,
  streaming,
  revealGlobalProposalId,
}: {
  chatSessionId: string;
  messages: ChatMessage[];
  streaming: string;
  /** The global variable suggestion a link points at, scrolled to once its card shows. */
  revealGlobalProposalId?: string | null;
}) {
  const { proposals, refresh } = useEditProposals({ chatSessionId });
  const suggestions = useGlobalVariableProposals({ chatSessionId });
  const loopProposals = useLoopTemplateProposals({ chatSessionId });
  const { head, afterMessage, tail } = placeChatProposals(messages, [
    ...(proposals ?? []).map((proposal) => ({
      ...proposal,
      card: (
        <EditProposalCard key={`proposal:${proposal.id}`} proposal={proposal} onSettled={refresh} />
      ),
    })),
    ...(suggestions.proposals ?? []).map((proposal) => ({
      ...proposal,
      card: (
        <GlobalVariableProposalCard
          key={`gvproposal:${proposal.id}`}
          proposal={proposal}
          onSettled={suggestions.refresh}
          reveal={proposal.id === revealGlobalProposalId ? revealCard : undefined}
        />
      ),
    })),
    ...(loopProposals.proposals ?? []).map((proposal) => ({
      ...proposal,
      card: (
        <LoopTemplateProposalCard
          key={`ltproposal:${proposal.id}`}
          proposal={proposal}
          onSettled={loopProposals.refresh}
        />
      ),
    })),
  ]);
  const cards = (list: { card: ReactNode }[] = []) => list.map((placed) => placed.card);

  const entries: ReactNode[] = [...cards(head)];
  for (const m of messages) {
    entries.push(
      <div key={`message:${m.id}`} className={`chat-msg chat-msg-${m.role}`}>
        <MarkdownRenderer content={m.content} className="chat-msg-content" />
        {m.interrupted && <span className="chat-interrupted">interrupted</span>}
      </div>,
      ...cards(afterMessage.get(m.id)),
    );
  }
  if (streaming) {
    entries.push(
      <div key="streaming" className="chat-msg chat-msg-assistant chat-msg-streaming">
        <MarkdownRenderer content={streaming} className="chat-msg-content" />
      </div>,
    );
  }
  entries.push(...cards(tail));
  return <>{entries}</>;
}
