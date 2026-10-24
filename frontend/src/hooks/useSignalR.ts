import { useEffect, useRef, useState, useCallback, useMemo } from "react";
import * as signalR from "@microsoft/signalr";
import type { TypedSignalRMessage } from "../types/signalr";
import { authService } from "../services/auth";

type MessageHandler<E extends string = string> = (message: TypedSignalRMessage<E>) => void;

const retryDelays = [0, 2_000, 10_000, 30_000];

/** The library default schedule, except that it keeps retrying every 30 s instead of giving up. */
const retryDelay = (previousRetryCount: number) =>
  retryDelays[Math.min(previousRetryCount, retryDelays.length - 1)];

const reconnectPolicy: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds: ({ previousRetryCount }) => retryDelay(previousRetryCount),
};

/** One hub connection as a component sees it; hooks that listen on one can be handed it. */
export type SignalRConnection = ReturnType<typeof useSignalR>;

/** `hubUrl` null opens no connection, for a caller that listens on another one instead. */
export function useSignalR(hubUrl: string | null) {
  const [connectionState, setConnectionState] = useState<
    "disconnected" | "connecting" | "connected" | "reconnecting"
  >("disconnected");
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const handlersRef = useRef<Map<string, Set<MessageHandler>>>(new Map());
  // Track which event types have a connection-level dispatcher so we register at most one.
  const dispatchersRef = useRef<Set<string>>(new Set());
  const [token, setToken] = useState<string | null>(() => authService.getToken());

  // Subscribe to auth token changes so the hook can reconnect on login/logout.
  useEffect(() => {
    return authService.onTokenChange((next) => setToken(next));
  }, []);

  const ensureDispatcher = useCallback((connection: signalR.HubConnection, eventType: string) => {
    if (dispatchersRef.current.has(eventType)) return;
    connection.on(eventType, (payload: unknown) => {
      const message: TypedSignalRMessage = {
        type: eventType,
        payload,
        timestamp: new Date().toISOString(),
      };
      handlersRef.current.get(eventType)?.forEach((h) => h(message));
    });
    dispatchersRef.current.add(eventType);
  }, []);

  const on = useCallback(
    <E extends string>(eventType: E, handler: MessageHandler<E>) => {
      const handlers = handlersRef.current.get(eventType) ?? new Set<MessageHandler>();
      handlers.add(handler as MessageHandler);
      handlersRef.current.set(eventType, handlers);

      // Attached in any state: a reconnect keeps the dispatchers it had but adds
      // none, so a handler added while reconnecting would otherwise never fire.
      const connection = connectionRef.current;
      if (connection) ensureDispatcher(connection, eventType);
    },
    [ensureDispatcher],
  );

  const off = useCallback(<E extends string>(eventType: E, handler: MessageHandler<E>) => {
    const handlers = handlersRef.current.get(eventType);
    handlers?.delete(handler as MessageHandler);
  }, []);

  useEffect(() => {
    if (!token || !hubUrl) return;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        // Read the token dynamically so reconnects use the latest value.
        accessTokenFactory: () => authService.getToken() ?? "",
      })
      .withAutomaticReconnect(reconnectPolicy)
      .configureLogging({ log: () => {} })
      .build();

    connectionRef.current = connection;
    dispatchersRef.current = new Set();

    const updateState = (state: signalR.HubConnectionState) => {
      setConnectionState(
        state === signalR.HubConnectionState.Connected
          ? "connected"
          : state === signalR.HubConnectionState.Connecting
            ? "connecting"
            : state === signalR.HubConnectionState.Reconnecting
              ? "reconnecting"
              : "disconnected",
      );
    };

    let stopped = false;
    let starting = false;
    let attempt = 0;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;

    const scheduleStart = () => {
      clearTimeout(retryTimer);
      retryTimer = setTimeout(() => void start(), retryDelay(attempt++));
    };

    // Every new ConnectionId starts outside the work-items group, so the hub is
    // reported connected only once it has joined: a reader that catches up on
    // "connected" must not miss what is sent before then. A join that fails
    // stops the connection, and onclose starts it again.
    const joinThenReportConnected = async () => {
      if (hubUrl === "/hubs/work-item") {
        try {
          await connection.invoke("SubscribeToWorkItems");
        } catch (err) {
          if (stopped) return;
          console.error(err);
          void connection.stop();
          return;
        }
      }
      if (stopped) return;
      attempt = 0;
      updateState(signalR.HubConnectionState.Connected);
    };

    connection.onreconnecting(() => {
      updateState(signalR.HubConnectionState.Reconnecting);
    });
    // .on() handlers survive automatic reconnects, so the dispatchers are still
    // registered; only the group has to be joined again.
    connection.onreconnected(() => void joinThenReportConnected());

    // The automatic reconnect does not retry a start() that fails, and it does not
    // restart a closed hub, so both are started again here. See
    // https://learn.microsoft.com/aspnet/core/signalr/javascript-client#automatically-reconnect
    const start = async () => {
      if (stopped || starting || connection.state !== signalR.HubConnectionState.Disconnected) {
        return;
      }
      starting = true;
      updateState(signalR.HubConnectionState.Connecting);
      try {
        await connection.start();
      } catch (err) {
        if (stopped) return; // expected during teardown
        console.error(err);
        updateState(signalR.HubConnectionState.Disconnected);
        scheduleStart();
        return;
      } finally {
        starting = false;
      }
      if (stopped) return;
      handlersRef.current.forEach((_, eventType) => ensureDispatcher(connection, eventType));
      await joinThenReportConnected();
    };

    connection.onclose(() => {
      // A connection torn down for a new token closes after its successor has
      // taken over the shared state, so it must not report it.
      if (stopped) return;
      updateState(signalR.HubConnectionState.Disconnected);
      scheduleStart();
    });

    const startNow = () => {
      clearTimeout(retryTimer);
      attempt = 0;
      void start();
    };
    const onVisibilityChange = () => {
      if (document.visibilityState === "visible") startNow();
    };
    document.addEventListener("visibilitychange", onVisibilityChange);
    window.addEventListener("online", startNow);

    void start();

    return () => {
      stopped = true;
      clearTimeout(retryTimer);
      document.removeEventListener("visibilitychange", onVisibilityChange);
      window.removeEventListener("online", startNow);
      void connection.stop();
      connectionRef.current = null;
      dispatchersRef.current = new Set();
    };
  }, [hubUrl, token, ensureDispatcher]);

  const invoke = useCallback((method: string, ...args: unknown[]) => {
    return connectionRef.current?.invoke(method, ...args);
  }, []);

  // One object per connection state, so a context that hands it down (the
  // shared work-item hub) re-renders its readers only when the state changes.
  return useMemo(() => ({ connectionState, on, off, invoke }), [connectionState, on, off, invoke]);
}
