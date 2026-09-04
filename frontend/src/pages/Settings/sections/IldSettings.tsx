import { useEffect, useState } from "react";
import { SchedulerSettingKeys } from "../../../services/auth";
import { NumericSettingField, SettingRow, ToggleSettingField } from "../controls";

/**
 * How the loop engine behaves: how much it runs at once, how far it goes on its
 * own, and how long it keeps what it produced.
 */
export default function IldSettings() {
  const [version, setVersion] = useState("");

  useEffect(() => {
    void fetch("/api/v1/health")
      .then((res) => res.json() as Promise<{ version?: string }>)
      .then((data) => setVersion(data.version ?? ""))
      .catch(() => {});
  }, []);

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
        <ToggleSettingField settingKey={SchedulerSettingKeys.IsPaused} label="Pause the scheduler">
          Nothing new starts while this is on. Runs already going carry on to their next node.
        </ToggleSettingField>
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
          <h3 className="settings-card-title">Provider interruptions</h3>
        </div>
        <ToggleSettingField
          settingKey={SchedulerSettingKeys.ThrottleAutoResume}
          label="Resume throttled runs automatically"
        >
          When the AI provider stops a step — a usage or session limit, a busy provider — the run
          waits in Human Feedback for you to click <strong>Resume</strong>. Turn this on to have ILD
          try the resume for you instead, never before the reset time the provider named. When the
          attempts below run out it leaves the run for you; resuming it yourself gives it a fresh
          set.
        </ToggleSettingField>
        <NumericSettingField
          settingKey={SchedulerSettingKeys.ThrottleRetryDelayMinutes}
          label="Wait between attempts"
          min={1}
          max={1440}
          fallback={60}
          unit="minutes"
        >
          How long a throttled run waits before each automatic attempt. A reset time the provider
          stated can push an attempt later than this, never earlier.
        </NumericSettingField>
        <NumericSettingField
          settingKey={SchedulerSettingKeys.ThrottleMaxRetries}
          label="Attempts before asking you"
          min={1}
          max={100}
          fallback={6}
        >
          How many automatic attempts a run may spend. Like the AI step cap, the count resets
          whenever you interact with the run.
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
