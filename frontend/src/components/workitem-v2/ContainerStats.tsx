import { useEffect, useState } from "react";
import { loopRunService } from "../../services/auth";
import type { ContainerStats as Stats } from "../../types";

/** How often the figures are read while the tab is open. */
export const STATS_POLL_MS = 2000;

const UNITS = ["B", "KiB", "MiB", "GiB", "TiB"];

export function formatBytes(bytes: number): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${unit === 0 ? value : value.toFixed(value >= 10 ? 0 : 1)} ${UNITS[unit]}`;
}

function formatCores(cores: number): string {
  return cores >= 10 ? cores.toFixed(0) : cores.toFixed(1).replace(/\.0$/, "");
}

interface MeterProps {
  label: string;
  usage: number;
  limit: number | null | undefined;
  format: (value: number) => string;
  unit?: string;
}

function Meter({ label, usage, limit, format, unit }: MeterProps) {
  const suffix = unit ? ` ${unit}` : "";
  if (!limit || limit <= 0) {
    return (
      <div className="wiv2-stat">
        <div className="wiv2-stat-head">
          <span className="wiv2-stat-label">{label}</span>
          <span className="wiv2-stat-value">
            {format(usage)}
            {suffix} <span className="wiv2-stat-note">no limit</span>
          </span>
        </div>
      </div>
    );
  }
  const percent = Math.min(100, Math.max(0, (usage / limit) * 100));
  return (
    <div className="wiv2-stat">
      <div className="wiv2-stat-head">
        <span className="wiv2-stat-label">{label}</span>
        <span className="wiv2-stat-value">
          {format(usage)} / {format(limit)}
          {suffix} · {percent.toFixed(0)}%
        </span>
      </div>
      <div
        className="wiv2-stat-bar"
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(percent)}
      >
        <div
          className={`wiv2-stat-fill${percent >= 90 ? " wiv2-stat-fill-high" : ""}`}
          style={{ width: `${percent}%` }}
        />
      </div>
    </div>
  );
}

/**
 * The run container's CPU, memory, network and disk use, read every couple of seconds while `active`. Network and disk
 * show only when the container engine exposes them.
 */
export default function ContainerStats({
  loopRunId,
  active,
}: {
  loopRunId: string;
  active: boolean;
}) {
  const [stats, setStats] = useState<Stats | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = async () => {
      try {
        const next = await loopRunService.getContainerStats(loopRunId);
        if (cancelled) return;
        setStats(next);
        setFailed(false);
      } catch {
        if (cancelled) return;
        setFailed(true);
      }
      // The next read starts once this one has answered, so a slow engine is never asked twice at once.
      if (!cancelled) timer = setTimeout(() => void read(), STATS_POLL_MS);
    };
    void read();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [loopRunId, active]);

  let body;
  if (stats === null && !failed) {
    body = <p className="wiv2-stats-note">Reading container stats…</p>;
  } else if (stats?.state === "running" && !failed) {
    const hasNetwork = stats.networkRxBytes != null || stats.networkTxBytes != null;
    const hasDisk = stats.diskReadBytes != null || stats.diskWriteBytes != null;
    body = (
      <div className="wiv2-stats-grid">
        {stats.cpuCores != null && (
          <Meter
            label="CPU"
            usage={stats.cpuCores}
            limit={stats.cpuLimitCores}
            format={formatCores}
            unit="CPU"
          />
        )}
        {stats.memoryBytes != null && (
          <Meter
            label="Memory"
            usage={stats.memoryBytes}
            limit={stats.memoryLimitBytes}
            format={formatBytes}
          />
        )}
        {hasNetwork && (
          <div className="wiv2-stat">
            <div className="wiv2-stat-head">
              <span className="wiv2-stat-label">Network</span>
              <span className="wiv2-stat-value">
                ↓ {formatBytes(stats.networkRxBytes ?? 0)} · ↑{" "}
                {formatBytes(stats.networkTxBytes ?? 0)}
              </span>
            </div>
          </div>
        )}
        {hasDisk && (
          <div className="wiv2-stat">
            <div className="wiv2-stat-head">
              <span className="wiv2-stat-label">Disk</span>
              <span className="wiv2-stat-value">
                read {formatBytes(stats.diskReadBytes ?? 0)} · written{" "}
                {formatBytes(stats.diskWriteBytes ?? 0)}
              </span>
            </div>
          </div>
        )}
      </div>
    );
  } else if (stats?.state === "stopped" && !failed) {
    body = <p className="wiv2-stats-note">Container not running</p>;
  } else {
    body = <p className="wiv2-stats-note">Stats unavailable</p>;
  }

  return (
    <div className="wiv2-container-stats" aria-label="Container stats" role="region">
      {body}
    </div>
  );
}
