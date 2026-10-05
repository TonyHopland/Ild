import { useEffect, useRef, useState } from "react";
import { settingsService } from "../../services/auth";

interface SettingRowProps {
  label: React.ReactNode;
  help?: React.ReactNode;
  htmlFor?: string;
  children?: React.ReactNode;
}

/** A labelled setting: copy on the left, whatever changes it on the right. */
export function SettingRow({ label, help, htmlFor, children }: SettingRowProps) {
  return (
    <div className="settings-row">
      <div className="settings-row-copy">
        <label className="settings-row-label" htmlFor={htmlFor}>
          {label}
        </label>
        {help && <p className="settings-row-help">{help}</p>}
      </div>
      {children && <div className="settings-row-control">{children}</div>}
    </div>
  );
}

interface SwitchProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  disabled?: boolean;
}

export function Switch({ checked, onChange, label, disabled = false }: SwitchProps) {
  return (
    <span className="settings-switch">
      <input
        type="checkbox"
        checked={checked}
        aria-label={label}
        disabled={disabled}
        onChange={(e) => onChange(e.target.checked)}
      />
      <span className="settings-switch-track" />
    </span>
  );
}

interface SegmentedProps<T extends string> {
  /** `null` presses nothing, for a choice that has not been made yet. */
  value: T | null;
  options: { value: T; label: string }[];
  onChange: (value: T) => void;
  label: string;
}

export function Segmented<T extends string>({
  value,
  options,
  onChange,
  label,
}: SegmentedProps<T>) {
  return (
    <div className="settings-segmented" role="group" aria-label={label}>
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-pressed={value === option.value}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}

interface ToggleSettingFieldProps {
  /** The app-setting key this switch reads and writes. */
  settingKey: string;
  label: string;
  /** Help text beside the switch. */
  children?: React.ReactNode;
}

/**
 * One boolean app setting, saved the moment it is flipped — there is nothing to
 * validate and nothing to draft, so a Save button would only be a step between
 * the user and the one bit they came to change. A save that fails puts the
 * switch back and says why, rather than leaving the page claiming a setting the
 * server never took.
 *
 * What it shows is the server's answer or the user's own act, never a stale mix:
 * it keeps the value the server last confirmed, and a failed save puts that back
 * rather than the opposite of the flip. A first read still on its way is the
 * server's answer until one of our saves lands — after that it is older than what
 * is stored — and it waits out a save in flight rather than undoing the flip
 * mid-save. The switch takes no second flip while a save is out, so two saves can
 * never land in the wrong order. A read that fails says so.
 */
export function useToggleSetting(settingKey: string) {
  const [checked, setChecked] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const confirmedRef = useRef<boolean | null>(null);
  const savedRef = useRef(false);
  const savingRef = useRef(false);

  useEffect(() => {
    let cancelled = false;
    confirmedRef.current = null;
    savedRef.current = false;
    const current = () => !cancelled && !savedRef.current;
    void settingsService
      .get(settingKey)
      // Case-insensitive because the API validates with bool.TryParse: a value
      // written as "True" is on to every backend reader, and a switch showing it
      // off would be the only thing in the system that disagrees.
      .then((s) => {
        if (!current()) return;
        confirmedRef.current = s.value.toLowerCase() === "true";
        if (!savingRef.current) setChecked(confirmedRef.current);
      })
      .catch((err: unknown) => {
        if (current()) {
          setError(
            `Could not load this setting: ${err instanceof Error ? err.message : "request failed"}`,
          );
        }
      });
    return () => {
      cancelled = true;
    };
  }, [settingKey]);

  const save = async (next: boolean) => {
    if (savingRef.current) return;
    savingRef.current = true;
    setChecked(next);
    setError(null);
    setSaving(true);
    try {
      await settingsService.put(settingKey, next ? "true" : "false");
      confirmedRef.current = next;
      savedRef.current = true;
      setError(null);
    } catch (err) {
      setChecked(confirmedRef.current ?? false);
      setError(err instanceof Error ? err.message : "Failed to save.");
    } finally {
      savingRef.current = false;
      setSaving(false);
    }
  };

  return { checked, error, saving, save };
}

/** A switch for one boolean app setting; see {@link useToggleSetting}. */
export function ToggleSettingField({ settingKey, label, children }: ToggleSettingFieldProps) {
  const { checked, error, saving, save } = useToggleSetting(settingKey);

  return (
    <SettingRow
      label={label}
      help={
        <>
          {children}
          {error && <span className="settings-error"> {error}</span>}
        </>
      }
    >
      <Switch checked={checked} onChange={(v) => void save(v)} label={label} disabled={saving} />
    </SettingRow>
  );
}

interface NumericSettingFieldProps {
  /** The app-setting key this field reads and writes. Also the input's id. */
  settingKey: string;
  label: string;
  min: number;
  max: number;
  /** Shown while the current value is still being fetched, or if it cannot be. */
  fallback: number;
  /** Replaces `min` in the range message, e.g. `"0 (disabled)"`. */
  minLabel?: string;
  /** Unit shown after the input, e.g. `days`. */
  unit?: string;
  /** Help text beside the field. */
  children?: React.ReactNode;
}

/**
 * One integer app setting: shows its current value, refuses one outside the
 * allowed range before going near the server, and reports whatever the save
 * said. The draft, the error and the in-flight flag are its own because nothing
 * outside the field reads them — a page renders several of these and holds no
 * state for any of them.
 */
export function NumericSettingField({
  settingKey,
  label,
  min,
  max,
  fallback,
  minLabel,
  unit,
  children,
}: NumericSettingFieldProps) {
  const [saved, setSaved] = useState<number>(fallback);
  const [draft, setDraft] = useState<string>(String(fallback));
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void settingsService
      .get(settingKey)
      .then((s) => {
        const n = parseInt(s.value, 10);
        if (Number.isNaN(n)) return;
        setSaved(n);
        setDraft(String(n));
      })
      // Unreachable API: leave the default showing rather than an empty box.
      .catch(() => {});
  }, [settingKey]);

  const save = async () => {
    const n = parseInt(draft, 10);
    if (Number.isNaN(n) || n < min || n > max) {
      setError(`Must be an integer between ${minLabel ?? min} and ${max}.`);
      return;
    }
    setError(null);
    setSaving(true);
    try {
      await settingsService.put(settingKey, String(n));
      setSaved(n);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save.");
    } finally {
      setSaving(false);
    }
  };

  return (
    <SettingRow
      label={label}
      htmlFor={settingKey}
      help={
        <>
          {children}
          {error && <span className="settings-error"> {error}</span>}
        </>
      }
    >
      <input
        id={settingKey}
        className="settings-input"
        type="number"
        min={min}
        max={max}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        style={{ width: "5rem" }}
      />
      {unit && <span className="settings-row-help">{unit}</span>}
      <button
        type="button"
        className="btn btn-primary"
        onClick={() => void save()}
        disabled={saving || draft === String(saved)}
      >
        Save
      </button>
    </SettingRow>
  );
}
