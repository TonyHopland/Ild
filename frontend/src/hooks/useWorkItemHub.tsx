import { createContext, useContext, type ReactNode } from "react";
import { useSignalR, type SignalRConnection } from "./useSignalR";

const WorkItemHubContext = createContext<SignalRConnection | null>(null);

/**
 * One work-item hub connection for the signed-in app, which the header's
 * counts, the scheduler state and the tab bar all listen on, instead of each
 * opening a connection of its own.
 */
export function WorkItemHubProvider({ children }: { children: ReactNode }) {
  const hub = useSignalR("/hubs/work-item");
  return <WorkItemHubContext.Provider value={hub}>{children}</WorkItemHubContext.Provider>;
}

/** The app's shared work-item hub connection, or null outside WorkItemHubProvider. */
export function useSharedWorkItemHub(): SignalRConnection | null {
  return useContext(WorkItemHubContext);
}
