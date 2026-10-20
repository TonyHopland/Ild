import { useCallback, useEffect, useState } from "react";
import { SchedulerSettingKeys, settingsService } from "../services/auth";
import type { TypedSignalRMessage } from "../types/signalr";
import { errorMessage } from "../utils/errorMessage";
import { useSignalR } from "./useSignalR";

/**
 * Whether the scheduler is running, read from the server and kept live over the
 * work-item hub, and a way to change it. The header's chip and the Settings
 * switch both read this, so they always say the same thing: on means running.
 */
export function useSchedulerState() {
  // null until the server has said, so nothing claims a state it has not read.
  const [running, setRunning] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { on, off, connectionState } = useSignalR();

  const load = useCallback(async () => {
    try {
      const setting = await settingsService.get(SchedulerSettingKeys.IsPaused);
      setRunning(setting.value !== "true");
    } catch {
      // Keep the last-known state if the read fails.
    }
  }, []);

  // Read on mount and again after every reconnect, since changes made while
  // disconnected arrive by neither path.
  useEffect(() => {
    if (connectionState === "connected" || connectionState === "disconnected") void load();
  }, [connectionState, load]);

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

  return { running, busy, error, set, clearError: () => setError(null) };
}
