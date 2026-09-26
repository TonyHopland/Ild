import type { ReactNode } from "react";
import EditProposalCard from "./EditProposalCard";
import MarkdownRenderer from "./MarkdownRenderer";
import { placeChatProposals } from "./editProposalPlacement";
import { useEditProposals } from "../hooks/useEditProposals";
import type { ChatMessage, WorkItemEditProposal } from "../types";

/**
 * One chat's transcript with the edit proposals it made, each after the reply
 * of the turn that made it, then the reply still streaming. Render it keyed by
 * the chat's id.
 *
 * Everything is one keyed list, so a card that moves to a new slot (its reply
 * landing) is moved rather than remounted, and keeps a decision in progress.
 */
export default function ChatTranscript({
  chatSessionId,
  messages,
  streaming,
}: {
  chatSessionId: string;
  messages: ChatMessage[];
  streaming: string;
}) {
  const { proposals, refresh } = useEditProposals({ chatSessionId });
  const { head, afterMessage, tail } = placeChatProposals(messages, proposals ?? []);
  const cards = (list: WorkItemEditProposal[] = []) =>
    list.map((proposal) => (
      <EditProposalCard key={`proposal:${proposal.id}`} proposal={proposal} onSettled={refresh} />
    ));

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
