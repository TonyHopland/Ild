import { useCallback, useEffect, useState } from "react";
import { SchedulerSettingKeys, settingsService } from "../services/auth";
import type { TypedSignalRMessage } from "../types/signalr";
import { errorMessage } from "../utils/errorMessage";
import { useJoinThenRefresh } from "./useJoinThenRefresh";
import { useSignalR, type SignalRConnection } from "./useSignalR";
import { useSharedWorkItemHub } from "./useWorkItemHub";

/**
 * Whether the scheduler is running, read from the server and kept live over the
 * work-item hub, and a way to change it. The header's chip and the Settings
 * switch both read this, so they always say the same thing: on means running.
 * `hub` is a work-item hub connection the caller already holds; without it the
 * hook uses the app's shared one, and opens its own only where there is none.
 */
export function useSchedulerState(hub?: SignalRConnection) {
  // null until the server has said, so nothing claims a state it has not read.
  const [running, setRunning] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const shared = useSharedWorkItemHub();
  const given = hub ?? shared;
  const own = useSignalR(given ? null : "/hubs/work-item");
  const connection = given ?? own;
  const { on, off } = connection;

  const load = useCallback(async () => {
    try {
      const setting = await settingsService.get(SchedulerSettingKeys.IsPaused);
      setRunning(setting.value !== "true");
    } catch {
      // Keep the last-known state if the read fails.
    }
  }, []);
  const refresh = useCallback(() => void load(), [load]);

  // Read once at once, so the state shows even while the hub is still connecting.
  useEffect(() => refresh(), [refresh]);
  // SchedulerStateChanged goes to the board's group only: join it on every
  // (re)connect, then read again, since a change made while out arrives by
  // neither path.
  useJoinThenRefresh(connection, null, refresh);

  useEffect(() => {
    const onChanged = (message: TypedSignalRMessage<"SchedulerStateChanged">) => {
      setRunning(!message.payload.isPaused);
    };
    on("SchedulerStateChanged", onChanged);
    return () => off("SchedulerStateChanged", onChanged);
  }, [on, off]);

  const set = useCallback(async (next: boolean) => {
    setBusy(true);
    setError(null);
    try {
      await settingsService.put(SchedulerSettingKeys.IsPaused, next ? "false" : "true");
      setRunning(next);
    } catch (err) {
      setError(errorMessage(err, "Failed to update the scheduler."));
    } finally {
      setBusy(false);
    }
  }, []);

  const clearError = useCallback(() => setError(null), []);
  return { running, busy, error, set, clearError };
}
