import { useEffect, useRef, useState, useCallback } from "react";
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

export function useSignalR(hubUrl = "/hubs/work-item") {
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

      const connection = connectionRef.current;
      if (connection?.state === signalR.HubConnectionState.Connected) {
        ensureDispatcher(connection, eventType);
      }
    },
    [ensureDispatcher],
  );

  const off = useCallback(<E extends string>(eventType: E, handler: MessageHandler<E>) => {
    const handlers = handlersRef.current.get(eventType);
    handlers?.delete(handler as MessageHandler);
  }, []);

  useEffect(() => {
    if (!token) return;

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

    connection.onreconnecting(() => {
      updateState(signalR.HubConnectionState.Reconnecting);
    });
    connection.onreconnected(() => {
      // .on() handlers survive automatic reconnects, so the dispatchers are still
      // registered. A reconnect produces a new ConnectionId, though, so the
      // work-items group membership is lost and must be re-joined.
      if (hubUrl === "/hubs/work-item") {
        connection.invoke("SubscribeToWorkItems").catch((err) => console.error(err));
      }
      updateState(signalR.HubConnectionState.Connected);
    });

    let stopped = false;
    let starting = false;
    let attempt = 0;
    let retryTimer: ReturnType<typeof setTimeout> | undefined;

    const scheduleStart = () => {
      clearTimeout(retryTimer);
      retryTimer = setTimeout(() => void start(), retryDelay(attempt++));
    };

    // The automatic reconnect gives up on a connection that was never up and
    // does not cover a start() that fails, so a closed hub is started again here.
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
      attempt = 0;
      updateState(signalR.HubConnectionState.Connected);
      handlersRef.current.forEach((_, eventType) => ensureDispatcher(connection, eventType));
      if (hubUrl === "/hubs/work-item") {
        connection.invoke("SubscribeToWorkItems").catch((err) => console.error(err));
      }
    };

    connection.onclose(() => {
      updateState(signalR.HubConnectionState.Disconnected);
      if (!stopped) scheduleStart();
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

  return { connectionState, on, off, invoke };
}
