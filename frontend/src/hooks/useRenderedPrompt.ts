import { useState, useEffect } from "react";
import { loopRunService } from "../services/auth";

/**
 * Fetches the rendered prompt for a suspended node by walking the event log
 * backwards for a specific event type. Returns null when the run id is
 * missing, the event is not found, or an error occurs.
 */
export default function useRenderedPrompt(
  runId: string | undefined,
  eventType: string,
): string | null {
  const [prompt, setPrompt] = useState<string | null>(null);

  useEffect(() => {
    if (!runId) {
      setPrompt(null);
      return;
    }
    let cancelled = false;
    void (async () => {
      try {
        const page = await loopRunService.getEvents(runId, 0, 500);
        if (cancelled) return;
        const rendered = [...(page.entries || [])].reverse().find((e) => e.eventType === eventType);
        setPrompt(rendered?.payload ?? null);
      } catch {
        if (!cancelled) setPrompt(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [runId, eventType]);

  return prompt;
}
