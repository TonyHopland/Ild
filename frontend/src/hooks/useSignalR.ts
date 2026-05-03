import { useEffect, useRef, useState, useCallback } from "react";
import * as signalR from "@microsoft/signalr";
import type { TypedSignalRMessage } from "../types/signalr";
import { authService } from "../services/auth";

type MessageHandler<E extends string = string> = (message: TypedSignalRMessage<E>) => void;

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
      .withAutomaticReconnect()
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

    connection.onreconnecting(() => updateState(signalR.HubConnectionState.Reconnecting));
    connection.onreconnected(() => updateState(signalR.HubConnectionState.Connected));
    connection.onclose(() => updateState(signalR.HubConnectionState.Disconnected));

    let stopped = false;

    void connection
      .start()
      .then(async () => {
        if (stopped) return;
        updateState(signalR.HubConnectionState.Connected);
        handlersRef.current.forEach((_, eventType) => ensureDispatcher(connection, eventType));
        await connection.invoke("SubscribeToWorkItems");
      })
      .catch((err) => {
        if (stopped) return; // expected during teardown
        console.error(err);
      });

    return () => {
      stopped = true;
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
