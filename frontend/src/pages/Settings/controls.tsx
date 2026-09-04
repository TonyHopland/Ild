import { useEffect, useState } from "react";
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
}

export function Switch({ checked, onChange, label }: SwitchProps) {
  return (
    <span className="settings-switch">
      <input
        type="checkbox"
        checked={checked}
        aria-label={label}
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
