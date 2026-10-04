import { useEffect, useState } from "react";
import { AiProviderTagField } from "../../../components/AiProviderTagField";
import { aiProviderService, ChatTitleSettingKeys, settingsService } from "../../../services/auth";
import type { AiProvider } from "../../../types";
import { SettingRow, Switch, useToggleSetting } from "../controls";

const SmartTitlesLabel = "Smart session titles";

/**
 * Whether chats are titled by a model summarising their first exchange, and the
 * provider tag that model runs on — the loop node's own field, resolved the same
 * way. The tag only matters while the switch is on, so it is shut while it is off.
 */
export default function ChatTitlesSettings() {
  const smartTitles = useToggleSetting(ChatTitleSettingKeys.SmartTitles);
  const [providers, setProviders] = useState<AiProvider[]>([]);
  const [saved, setSaved] = useState("");
  const [draft, setDraft] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void aiProviderService
      .getAll()
      .then(setProviders)
      // Unreachable API: no suggestions, and the field says nothing can run.
      .catch(() => {});
    void settingsService
      .get(ChatTitleSettingKeys.TitleProviderTag)
      .then((s) => {
        setSaved(s.value);
        // Typed before the stored tag arrived: what was typed stays.
        setDraft((current) => (current === "" ? s.value : current));
      })
      .catch(() => {});
  }, []);

  const save = async () => {
    const sent = draft;
    setError(null);
    setSaving(true);
    try {
      const stored = await settingsService.put(ChatTitleSettingKeys.TitleProviderTag, sent);
      setSaved(stored.value);
      // Edited again while saving: the newer draft stays.
      setDraft((current) => (current === sent ? stored.value : current));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save.");
    } finally {
      setSaving(false);
    }
  };

  const off = !smartTitles.checked;

  return (
    <section className="settings-card">
      <div className="settings-card-header">
        <h3 className="settings-card-title">Chat titles</h3>
      </div>
      <SettingRow
        label={SmartTitlesLabel}
        help={
          <>
            After a chat&apos;s first reply, ask a model for a short title that says what the chat
            is about. While this is off, a chat is titled with the start of its first message and no
            model is asked. Titles already made stay when it is turned off, and a chat you renamed
            is never retitled.
            {smartTitles.error && <span className="settings-error"> {smartTitles.error}</span>}
          </>
        }
      >
        <Switch
          checked={smartTitles.checked}
          onChange={(v) => void smartTitles.save(v)}
          label={SmartTitlesLabel}
        />
      </SettingRow>
      <div className="settings-row settings-provider-tag">
        <div className="settings-row-copy">
          <AiProviderTagField
            id="chat-title-provider-tag"
            tag={draft}
            providers={providers}
            onChange={setDraft}
            disabled={off}
            cannotRun="no title can be generated"
          />
          {error && <span className="settings-error">{error}</span>}
        </div>
        <div className="settings-row-control">
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => void save()}
            disabled={off || saving || draft === saved}
          >
            Save
          </button>
        </div>
      </div>
    </section>
  );
}
