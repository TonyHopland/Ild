import { ChatRetentionSettingKeys } from "../../../services/auth";
import { NumericSettingField, ToggleSettingField } from "../controls";

/** The switch that deletes chats nobody has written in for a while, and how long that is. */
export default function ChatRetentionSettings() {
  return (
    <section className="settings-card">
      <div className="settings-card-header">
        <h3 className="settings-card-title">Chat history</h3>
      </div>
      <ToggleSettingField
        settingKey={ChatRetentionSettingKeys.AutoDeleteInactive}
        label="Delete inactive chats"
      >
        Delete every chat, of every user, that has had no new message for the number of days below.
        A deleted chat cannot be brought back. Starred chats are kept until their star is removed.
        Turning this on also deletes the chats that are already inactive, within the hour.
      </ToggleSettingField>
      <NumericSettingField
        settingKey={ChatRetentionSettingKeys.AutoDeleteInactiveDays}
        label="Delete chats inactive for"
        min={1}
        max={3650}
        fallback={14}
        unit="days"
        saveLabel="Save inactive chat days"
      >
        Used while the switch above is on; set it before turning that on. Counted from a chat&apos;s
        newest message, or from when it was started if it has none. Renaming, starring or reading a
        chat does not count.
      </NumericSettingField>
    </section>
  );
}
