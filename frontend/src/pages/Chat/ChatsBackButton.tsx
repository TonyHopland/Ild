import Icon from "../../components/Icon";

/** On a phone, the way from an open chat or the schedules back to the chat list. */
export default function ChatsBackButton({ onClick }: { onClick: () => void }) {
  return (
    <button type="button" className="btn btn-ghost chat-page-back" onClick={onClick}>
      <Icon name="back" />
      Chats
    </button>
  );
}
