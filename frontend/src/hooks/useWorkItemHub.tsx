import { createContext, useContext, type ReactNode } from "react";
import { useSignalR, type SignalRConnection } from "./useSignalR";

const WorkItemHubContext = createContext<SignalRConnection | null>(null);

/**
 * One work-item hub connection for the signed-in app, which every page and
 * hook that listens to the board shares, instead of each opening a connection
 * of its own.
 */
export function WorkItemHubProvider({ children }: { children: ReactNode }) {
  const hub = useSignalR("/hubs/work-item");
  return <WorkItemHubContext.Provider value={hub}>{children}</WorkItemHubContext.Provider>;
}

/** The app's shared work-item hub connection. Only valid inside WorkItemHubProvider. */
export function useSharedWorkItemHub(): SignalRConnection {
  const context = useContext(WorkItemHubContext);
  if (!context) {
    throw new Error("useSharedWorkItemHub must be used within a WorkItemHubProvider");
  }
  return context;
}
