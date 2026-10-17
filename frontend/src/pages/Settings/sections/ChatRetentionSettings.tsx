import { ChatRetentionSettingKeys } from "../../../services/auth";
import { NumericSettingField, SettingRow, Switch, useToggleSetting } from "../controls";

const AutoDeleteLabel = "Delete inactive chats";

/** The switch that deletes chats nobody has written in for a while, and how long that is. */
export default function ChatRetentionSettings() {
  const autoDelete = useToggleSetting(ChatRetentionSettingKeys.AutoDeleteInactive);

  return (
    <section className="settings-card">
      <div className="settings-card-header">
        <h3 className="settings-card-title">Chat history</h3>
      </div>
      <SettingRow
        label={AutoDeleteLabel}
        help={
          <>
            Delete every chat, of every user, that has had no new message for the number of days
            below. A deleted chat cannot be brought back. Starred chats are kept until their star is
            removed. Turning this on also deletes the chats that are already inactive, within the
            hour.
            {autoDelete.error && (
              <span className="settings-error" role="alert">
                {" "}
                {autoDelete.error}
              </span>
            )}
          </>
        }
      >
        <Switch
          checked={autoDelete.checked}
          onChange={(v) => void autoDelete.save(v)}
          label={AutoDeleteLabel}
          disabled={autoDelete.saving}
        />
      </SettingRow>
      <NumericSettingField
        settingKey={ChatRetentionSettingKeys.AutoDeleteInactiveDays}
        label="Delete chats inactive for"
        min={1}
        max={3650}
        fallback={14}
        unit="days"
        disabled={!autoDelete.checked}
        saveLabel="Save inactive chat days"
      >
        Counted from a chat&apos;s newest message, or from when it was started if it has none.
        Renaming, starring or reading a chat does not count.
      </NumericSettingField>
    </section>
  );
}
