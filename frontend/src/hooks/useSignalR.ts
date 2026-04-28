import { useEffect, useRef, useState, useCallback } from "react";
import * as signalR from "@microsoft/signalr";
import { SignalRMessage } from "../types";
import { authService } from "../services/auth";

type MessageHandler = (message: SignalRMessage) => void;

export function useSignalR(hubUrl = "/hubs/work-item") {
  const [connectionState, setConnectionState] = useState<
    "disconnected" | "connecting" | "connected" | "reconnecting"
  >("disconnected");
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const handlersRef = useRef<Map<string, Set<MessageHandler>>>(new Map());

  const on = useCallback((eventType: string, handler: MessageHandler) => {
    const handlers = handlersRef.current.get(eventType) || new Set();
    handlers.add(handler);
    handlersRef.current.set(eventType, handlers);

    if (connectionRef.current?.state === signalR.HubConnectionState.Connected) {
      connectionRef.current.on(eventType, (payload: unknown) => {
        handler({
          type: eventType,
          payload,
          timestamp: new Date().toISOString(),
        });
      });
    }
  }, []);

  const off = useCallback((eventType: string, handler: MessageHandler) => {
    const handlers = handlersRef.current.get(eventType);
    handlers?.delete(handler);
  }, []);

  useEffect(() => {
    const token = authService.getToken();
    if (!token) return;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: () => token,
      })
      .withAutomaticReconnect()
      .build();

    connectionRef.current = connection;

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

    void connection.start().then(() => {
      updateState(signalR.HubConnectionState.Connected);

      handlersRef.current.forEach((handlers, eventType) => {
        connection.on(eventType, (payload: unknown) => {
          const message: SignalRMessage = {
            type: eventType,
            payload,
            timestamp: new Date().toISOString(),
          };
          handlers.forEach((handler) => handler(message));
        });
      });
    });

    return () => {
      void connection.stop();
      connectionRef.current = null;
    };
  }, [hubUrl]);

  return { connectionState, on, off };
}
