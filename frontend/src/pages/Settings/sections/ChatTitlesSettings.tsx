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
  // Null until read: the field states which provider a tag runs on, and edits the
  // stored tag, only once it knows both.
  const [providers, setProviders] = useState<AiProvider[] | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [loadErrors, setLoadErrors] = useState<string[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    const failed = (what: string) => (err: unknown) => {
      if (cancelled) return;
      const reason = err instanceof Error ? err.message : "request failed";
      setLoadErrors((current) => [...current, `Could not load ${what}: ${reason}`]);
    };
    void aiProviderService
      .getAll()
      .then((loaded) => {
        if (!cancelled) setProviders(loaded);
      })
      .catch(failed("the AI providers"));
    void settingsService
      .get(ChatTitleSettingKeys.TitleProviderTag)
      .then((s) => {
        if (cancelled) return;
        setSaved(s.value);
        setDraft(s.value);
      })
      .catch(failed("the stored provider tag"));
    return () => {
      cancelled = true;
    };
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
  const loaded = providers !== null && saved !== null;

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
            disabled={off || !loaded}
            cannotRun="no title can be generated"
          />
          {loadErrors.map((message) => (
            <span key={message} className="settings-error">
              {message}
            </span>
          ))}
          {error && <span className="settings-error">{error}</span>}
        </div>
        <div className="settings-row-control">
          <button
            type="button"
            className="btn btn-primary"
            onClick={() => void save()}
            disabled={off || !loaded || saving || draft === saved}
          >
            Save
          </button>
        </div>
      </div>
    </section>
  );
}
