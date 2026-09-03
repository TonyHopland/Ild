import { useEffect, useState } from "react";
import { SchedulerSettingKeys, settingsService } from "../../../services/auth";
import { NumericSettingField, SettingRow, Switch } from "../controls";

/**
 * How the loop engine behaves: how much it runs at once, how far it goes on its
 * own, and how long it keeps what it produced.
 */
export default function IldSettings() {
  const [paused, setPaused] = useState(false);
  const [pauseError, setPauseError] = useState<string | null>(null);
  const [version, setVersion] = useState("");

  useEffect(() => {
    void fetch("/api/v1/health")
      .then((res) => res.json() as Promise<{ version?: string }>)
      .then((data) => setVersion(data.version ?? ""))
      .catch(() => {});
  }, []);

  useEffect(() => {
    void settingsService
      .get(SchedulerSettingKeys.IsPaused)
      .then((s) => setPaused(s.value === "true"))
      .catch(() => {});
  }, []);

  const togglePause = async (next: boolean) => {
    const previous = paused;
    setPaused(next);
    setPauseError(null);
    try {
      await settingsService.put(SchedulerSettingKeys.IsPaused, next ? "true" : "false");
    } catch (err) {
      setPaused(previous);
      setPauseError(err instanceof Error ? err.message : "Failed to save.");
    }
  };

  return (
    <>
      <div className="settings-pane-header">
        <h2>Ild</h2>
        <p>Scheduling, autonomy and retention for the loop engine.</p>
      </div>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Scheduler</h3>
        </div>
        <SettingRow
          label="Pause the scheduler"
          help={
            <>
              Nothing new starts while this is on. Runs already going carry on to their next node.
              {pauseError && <span className="settings-error"> {pauseError}</span>}
            </>
          }
        >
          <Switch
            checked={paused}
            onChange={(v) => void togglePause(v)}
            label="Pause the scheduler"
          />
        </SettingRow>
        <NumericSettingField
          settingKey={SchedulerSettingKeys.MaxConcurrent}
          label="Max concurrent running work items"
          min={1}
          max={1000}
          fallback={5}
        >
          Caps how many work items the scheduler will run at once. Per-provider parallelism is
          configured on each AI provider.
        </NumericSettingField>
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Autonomy</h3>
        </div>
        <NumericSettingField
          settingKey={SchedulerSettingKeys.MaxAiTraversals}
          label="Max AI steps between human interactions"
          min={1}
          max={1000}
          fallback={25}
        >
          How many AI nodes a run may execute before it stops and asks you whether to carry on. The
          count resets every time you interact with the run, so a back-and-forth between you and the
          AI never reaches it — only a loop running on its own does. At the cap the run waits in
          Human Feedback.
        </NumericSettingField>
        <NumericSettingField
          settingKey={SchedulerSettingKeys.PrHeartbeatSeconds}
          label="Poll PR state every"
          min={5}
          max={3600}
          fallback={60}
          unit="seconds"
        >
          How often the background poller fetches PR state (CI, reviews, merge status) for runs
          parked at a PR node awaiting merge. Lower values react faster but use more provider API
          calls.
        </NumericSettingField>
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">Retention</h3>
        </div>
        <NumericSettingField
          settingKey={SchedulerSettingKeys.RunRetentionDays}
          label="Delete finished runs after"
          min={0}
          max={3650}
          fallback={30}
          minLabel="0 (disabled)"
          unit="days"
        >
          How long a finished run&apos;s worktree, branch, and history are kept before being
          reclaimed. Set to <strong>0</strong> to keep runs forever. Runs pinned with “Retain” are
          never deleted.
        </NumericSettingField>
      </section>

      <section className="settings-card">
        <div className="settings-card-header">
          <h3 className="settings-card-title">About</h3>
        </div>
        <SettingRow label="Version">
          <span className="settings-row-label">{version ? `v${version}-beta` : "—"}</span>
        </SettingRow>
        <SettingRow
          label="Integrated Loop Dashboard"
          help="A tool for managing work items and automation loops."
        />
      </section>
    </>
  );
}
