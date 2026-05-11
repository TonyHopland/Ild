import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";

const startMock = vi.fn().mockResolvedValue(undefined);
const stopMock = vi.fn().mockResolvedValue(undefined);
const invokeMock = vi.fn().mockResolvedValue(undefined);
const onMock = vi.fn();
const onreconnectingMock = vi.fn();
const onreconnectedMock = vi.fn();
const oncloseMock = vi.fn();
let connectionState = 1; // Connected

const fakeConnection = {
  start: startMock,
  stop: stopMock,
  invoke: invokeMock,
  on: onMock,
  onreconnecting: onreconnectingMock,
  onreconnected: onreconnectedMock,
  onclose: oncloseMock,
  get state() {
    return connectionState;
  },
};

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      withUrl() {
        return this;
      }
      withAutomaticReconnect() {
        return this;
      }
      configureLogging() {
        return this;
      }
      build() {
        return fakeConnection;
      }
    },
    HubConnectionState: {
      Disconnected: 0,
      Connected: 1,
      Connecting: 2,
      Reconnecting: 4,
    },
  };
});

import { useSignalR } from "./useSignalR";
import { authService } from "../services/auth";

beforeEach(() => {
  startMock.mockClear();
  stopMock.mockClear();
  invokeMock.mockClear();
  onMock.mockClear();
  localStorage.clear();
});

afterEach(() => {
  vi.clearAllMocks();
});

describe("useSignalR", () => {
  test("does not start a connection when there is no token", () => {
    const { result } = renderHook(() => useSignalR("/hubs/test"));
    expect(startMock).not.toHaveBeenCalled();
    expect(result.current.connectionState).toBe("disconnected");
  });

  test("starts a connection when a token is set", async () => {
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    const { unmount } = renderHook(() => useSignalR("/hubs/test"));
    await act(async () => {
      await Promise.resolve();
    });
    expect(startMock).toHaveBeenCalled();
    unmount();
  });

  test("on/off add and remove handlers without throwing", () => {
    const { result } = renderHook(() => useSignalR("/hubs/test"));
    const handler = vi.fn();
    act(() => {
      result.current.on("TestEvent", handler);
      result.current.off("TestEvent", handler);
    });
    expect(handler).not.toHaveBeenCalled();
  });

  test("does not auto-subscribe to work items when connected to a non-work-item hub", async () => {
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    const { unmount } = renderHook(() => useSignalR("/hubs/loop-run"));
    await act(async () => {
      await Promise.resolve();
    });
    expect(invokeMock).not.toHaveBeenCalledWith("SubscribeToWorkItems");
    unmount();
  });

  test("auto-subscribes to work items when connected to the work-item hub", async () => {
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    const { unmount } = renderHook(() => useSignalR("/hubs/work-item"));
    await act(async () => {
      await Promise.resolve();
    });
    expect(invokeMock).toHaveBeenCalledWith("SubscribeToWorkItems");
    unmount();
  });
});
