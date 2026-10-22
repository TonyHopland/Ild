import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";

// The stand-in follows @microsoft/signalr 10.0.11 (HubConnection.ts): a
// connection starts Disconnected, start() rejects unless it is Disconnected,
// moves to Connecting at once, and ends Connected or, on failure, Disconnected.
const startMock = vi.fn();
const stopMock = vi.fn().mockResolvedValue(undefined);
const invokeMock = vi.fn().mockResolvedValue(undefined);
const onMock = vi.fn();
const onreconnectingMock = vi.fn();
const onreconnectedMock = vi.fn();
const oncloseMock = vi.fn();
const reconnectArgs: unknown[] = [];
let connectionState = "Disconnected";
// How each start() ends: resolves unless a test queues a failure or a hold.
let startOutcomes: (() => Promise<void>)[] = [];

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

function fakeStart(): Promise<void> {
  if (connectionState !== "Disconnected") {
    return Promise.reject(
      new Error("Cannot start a HubConnection that is not in the 'Disconnected' state."),
    );
  }
  connectionState = "Connecting";
  const outcome = startOutcomes.shift() ?? (() => Promise.resolve());
  return outcome().then(
    () => {
      connectionState = "Connected";
    },
    (error: unknown) => {
      connectionState = "Disconnected";
      throw error;
    },
  );
}

vi.mock("@microsoft/signalr", () => {
  return {
    HubConnectionBuilder: class {
      withUrl() {
        return this;
      }
      withAutomaticReconnect(policy?: unknown) {
        reconnectArgs.push(policy);
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
      Disconnected: "Disconnected",
      Connecting: "Connecting",
      Connected: "Connected",
      Disconnecting: "Disconnecting",
      Reconnecting: "Reconnecting",
    },
  };
});

import { useSignalR } from "./useSignalR";
import { authService } from "../services/auth";

beforeEach(() => {
  connectionState = "Disconnected";
  startOutcomes = [];
  reconnectArgs.length = 0;
  startMock.mockReset();
  startMock.mockImplementation(fakeStart);
  stopMock.mockClear();
  invokeMock.mockClear();
  onMock.mockClear();
  onreconnectingMock.mockClear();
  onreconnectedMock.mockClear();
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

  test("re-joins the work-items group when the work-item hub reconnects", async () => {
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    const { unmount } = renderHook(() => useSignalR("/hubs/work-item"));
    await act(async () => {
      await Promise.resolve();
    });
    invokeMock.mockClear();

    const onreconnected = onreconnectedMock.mock.calls[0][0] as () => void;
    await act(async () => {
      onreconnected();
      await Promise.resolve();
    });

    expect(invokeMock).toHaveBeenCalledWith("SubscribeToWorkItems");
    unmount();
  });

  test("does not re-subscribe to work items when a non-work-item hub reconnects", async () => {
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    const { unmount } = renderHook(() => useSignalR("/hubs/loop-run"));
    await act(async () => {
      await Promise.resolve();
    });
    invokeMock.mockClear();

    const onreconnected = onreconnectedMock.mock.calls[0][0] as () => void;
    await act(async () => {
      onreconnected();
      await Promise.resolve();
    });

    expect(invokeMock).not.toHaveBeenCalledWith("SubscribeToWorkItems");
    unmount();
  });

  test("does not register a duplicate connection-level handler across a reconnect cycle", async () => {
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    const { result, unmount } = renderHook(() => useSignalR("/hubs/work-item"));
    await act(async () => {
      await Promise.resolve();
    });

    const handler = vi.fn();
    act(() => {
      result.current.on("WorkItemUpdated", handler);
    });

    const onForEvent = () =>
      onMock.mock.calls.filter(([eventType]) => eventType === "WorkItemUpdated").length;
    expect(onForEvent()).toBe(1);

    const onreconnecting = onreconnectingMock.mock.calls[0][0] as () => void;
    const onreconnected = onreconnectedMock.mock.calls[0][0] as () => void;
    await act(async () => {
      onreconnecting();
      onreconnected();
      await Promise.resolve();
    });

    expect(onForEvent()).toBe(1);
    unmount();
  });
  test("a handler added while the hub reconnects receives events once it is back", async () => {
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");

    const { result, unmount } = renderHook(() => useSignalR("/hubs/work-item"));
    await act(async () => {
      await Promise.resolve();
    });

    const onreconnecting = onreconnectingMock.mock.calls[0][0] as () => void;
    const onreconnected = onreconnectedMock.mock.calls[0][0] as () => void;
    connectionState = "Reconnecting";
    act(() => onreconnecting());

    const handler = vi.fn();
    act(() => {
      result.current.on("WorkItemRunProgressed", handler);
    });
    connectionState = "Connected";
    await act(async () => {
      onreconnected();
      await Promise.resolve();
    });

    const dispatchers = onMock.mock.calls.filter(
      ([eventType]) => eventType === "WorkItemRunProgressed",
    );
    expect(dispatchers).toHaveLength(1);
    (dispatchers[0][1] as (payload: unknown) => void)({ workItemId: "a" });
    expect(handler).toHaveBeenCalledWith(
      expect.objectContaining({ type: "WorkItemRunProgressed", payload: { workItemId: "a" } }),
    );
    unmount();
  });
});

describe("the connection object useSignalR returns", () => {
  test("stays the same object across renders while its state is unchanged, so a context handing it down does not re-render its readers", () => {
    const { result, rerender } = renderHook(() => useSignalR(null));
    const first = result.current;
    rerender();
    rerender();
    expect(result.current).toBe(first);
  });
});

describe("useSignalR after the hub has closed", () => {
  let visibility: DocumentVisibilityState = "visible";

  beforeEach(() => {
    visibility = "visible";
    Object.defineProperty(document, "visibilityState", {
      configurable: true,
      get: () => visibility,
    });
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "setInterval", "clearInterval"] });
    localStorage.setItem("auth_token", "tok-1");
    authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-1");
  });

  afterEach(() => {
    vi.useRealTimers();
    // Back to jsdom's own getter on the prototype.
    delete (document as unknown as Record<string, unknown>).visibilityState;
  });

  async function flush() {
    for (let i = 0; i < 5; i++) {
      await act(async () => {
        await Promise.resolve();
      });
    }
  }

  async function mountConnected(hubUrl = "/hubs/work-item") {
    const hook = renderHook(() => useSignalR(hubUrl));
    await flush();
    expect(hook.result.current.connectionState).toBe("connected");
    return hook;
  }

  /** What the library does once its reconnect attempts end: Disconnected, then onclose. */
  async function closeHub() {
    connectionState = "Disconnected";
    const onclose = oncloseMock.mock.calls[0][0] as (error?: Error) => void;
    await act(async () => {
      onclose(new Error("Server timeout elapsed."));
    });
    await flush();
  }

  async function becomeVisible() {
    await act(async () => {
      visibility = "visible";
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await flush();
  }

  async function comeOnline() {
    await act(async () => {
      window.dispatchEvent(new Event("online"));
    });
    await flush();
  }

  function hold() {
    let release!: () => void;
    let fail!: (error: Error) => void;
    const held = new Promise<void>((resolve, reject) => {
      release = resolve;
      fail = reject;
    });
    startOutcomes.push(() => held);
    return { release, fail };
  }

  test("the automatic-reconnect policy never gives up and never waits more than 30 seconds", async () => {
    const { unmount } = await mountConnected();

    const policy = reconnectArgs[0] as {
      nextRetryDelayInMilliseconds?: (context: {
        previousRetryCount: number;
        elapsedMilliseconds: number;
        retryReason: Error;
      }) => number | null;
    };
    expect(typeof policy?.nextRetryDelayInMilliseconds).toBe("function");
    for (let previousRetryCount = 0; previousRetryCount <= 60; previousRetryCount++) {
      const delay = policy.nextRetryDelayInMilliseconds!({
        previousRetryCount,
        elapsedMilliseconds: previousRetryCount * 30_000,
        retryReason: new Error("WebSocket closed with status code: 1006."),
      });
      expect(typeof delay).toBe("number");
      expect(delay).toBeGreaterThanOrEqual(0);
      expect(delay).toBeLessThanOrEqual(30_000);
    }
    unmount();
  });

  test.each([
    ["the tab becomes visible again", becomeVisible],
    ["the browser comes back online", comeOnline],
  ])("starts the hub again at once when %s", async (_, wake) => {
    const { result, unmount } = await mountConnected();
    await closeHub();
    expect(result.current.connectionState).toBe("disconnected");
    const before = startMock.mock.calls.length;

    await wake();

    expect(startMock.mock.calls.length).toBe(before + 1);
    expect(result.current.connectionState).toBe("connected");
    unmount();
  });

  test("a second wake-up while the restart is still in flight starts nothing more", async () => {
    const { result, unmount } = await mountConnected();
    await closeHub();
    const before = startMock.mock.calls.length;
    const restart = hold();

    await becomeVisible();
    await comeOnline();
    await becomeVisible();
    expect(startMock.mock.calls.length).toBe(before + 1);

    await act(async () => restart.release());
    await flush();
    expect(result.current.connectionState).toBe("connected");
    expect(startMock.mock.calls.length).toBe(before + 1);
    unmount();
  });

  test("a visibility change while the tab is hidden starts nothing", async () => {
    const { unmount } = await mountConnected();
    await closeHub();
    const before = startMock.mock.calls.length;

    await act(async () => {
      visibility = "hidden";
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await flush();

    expect(startMock.mock.calls.length).toBe(before);
    unmount();
  });

  test.each(["Connected", "Connecting", "Reconnecting"])(
    "a wake-up while the hub is %s starts nothing",
    async (state) => {
      const { unmount } = await mountConnected();
      connectionState = state;
      const before = startMock.mock.calls.length;

      await becomeVisible();
      await comeOnline();

      expect(startMock.mock.calls.length).toBe(before);
      unmount();
    },
  );

  test("a closed hub keeps being started again in the background until a start succeeds", async () => {
    const { result, unmount } = await mountConnected();
    await closeHub();
    const before = startMock.mock.calls.length;
    startOutcomes.push(() => Promise.reject(new Error("Failed to fetch")));
    startOutcomes.push(() => Promise.reject(new Error("Failed to fetch")));

    for (let wait = 0; wait < 3; wait++) {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30_000);
      });
      await flush();
    }

    expect(result.current.connectionState).toBe("connected");
    expect(startMock.mock.calls.length).toBe(before + 3);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(120_000);
    });
    expect(startMock.mock.calls.length).toBe(before + 3);
    unmount();
  });

  test("a first start that fails is retried after a backoff", async () => {
    startOutcomes.push(() => Promise.reject(new Error("Failed to fetch")));
    const { result, unmount } = renderHook(() => useSignalR("/hubs/loop-run"));
    await flush();
    expect(startMock).toHaveBeenCalledTimes(1);
    expect(result.current.connectionState).toBe("disconnected");

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    await flush();

    expect(startMock).toHaveBeenCalledTimes(2);
    expect(result.current.connectionState).toBe("connected");
    unmount();
  });

  test("a wake-up restarts a hub whose first start failed", async () => {
    startOutcomes.push(() => Promise.reject(new Error("Failed to fetch")));
    const { result, unmount } = renderHook(() => useSignalR("/hubs/loop-run"));
    await flush();
    expect(startMock).toHaveBeenCalledTimes(1);

    await comeOnline();

    expect(startMock).toHaveBeenCalledTimes(2);
    expect(result.current.connectionState).toBe("connected");
    unmount();
  });

  test("the work-item hub re-joins its group after a restart", async () => {
    const { unmount } = await mountConnected("/hubs/work-item");
    await closeHub();
    invokeMock.mockClear();

    await becomeVisible();

    expect(invokeMock).toHaveBeenCalledWith("SubscribeToWorkItems");
    unmount();
  });

  test("nothing starts the hub once the hook has unmounted", async () => {
    const { unmount } = await mountConnected();
    await closeHub();
    const before = startMock.mock.calls.length;

    unmount();
    await becomeVisible();
    await comeOnline();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300_000);
    });

    expect(startMock.mock.calls.length).toBe(before);
    expect(vi.getTimerCount()).toBe(0);
  });

  test("the connection replaced on a new token closing late leaves the new one connected", async () => {
    const { result, unmount } = await mountConnected();
    const closeOld = oncloseMock.mock.calls[0][0] as (error?: Error) => void;
    stopMock.mockImplementationOnce(() => {
      connectionState = "Disconnected";
      return Promise.resolve();
    });

    await act(async () => {
      authService.setAuth({ id: "u1", username: "x", createdAt: "" }, "tok-2");
    });
    await flush();
    expect(result.current.connectionState).toBe("connected");
    const starts = startMock.mock.calls.length;

    await act(async () => closeOld());
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300_000);
    });

    expect(result.current.connectionState).toBe("connected");
    expect(startMock.mock.calls.length).toBe(starts);
    unmount();
  });
  function holdJoin() {
    let release!: () => void;
    let fail!: (error: Error) => void;
    invokeMock.mockImplementationOnce(
      () =>
        new Promise<void>((resolve, reject) => {
          release = resolve;
          fail = reject;
        }),
    );
    return { release: () => release(), fail: (error: Error) => fail(error) };
  }

  test("the work-item hub is not reported connected until it has joined its group", async () => {
    const join = holdJoin();
    const { result, unmount } = renderHook(() => useSignalR("/hubs/work-item"));
    await flush();
    expect(invokeMock).toHaveBeenCalledWith("SubscribeToWorkItems");
    expect(result.current.connectionState).toBe("connecting");

    await act(async () => join.release());
    await flush();
    expect(result.current.connectionState).toBe("connected");
    unmount();
  });

  test("a reconnected work-item hub is not reported connected until it has joined its group again", async () => {
    const { result, unmount } = await mountConnected("/hubs/work-item");
    const onreconnecting = onreconnectingMock.mock.calls[0][0] as () => void;
    const onreconnected = onreconnectedMock.mock.calls[0][0] as () => void;
    const join = holdJoin();

    await act(async () => onreconnecting());
    await act(async () => onreconnected());
    await flush();
    expect(result.current.connectionState).toBe("reconnecting");

    await act(async () => join.release());
    await flush();
    expect(result.current.connectionState).toBe("connected");
    unmount();
  });

  test.each([
    ["starts", false],
    ["reconnects", true],
  ])(
    "a work-item hub that %s but cannot join its group is stopped and started again",
    async (_, reconnect) => {
      const join = reconnect ? undefined : holdJoin();
      const hook = renderHook(() => useSignalR("/hubs/work-item"));
      await flush();
      let failJoin = join?.fail;
      if (reconnect) {
        expect(hook.result.current.connectionState).toBe("connected");
        failJoin = holdJoin().fail;
        await act(async () => (onreconnectingMock.mock.calls[0][0] as () => void)());
        await act(async () => (onreconnectedMock.mock.calls[0][0] as () => void)());
        await flush();
      }
      const starts = startMock.mock.calls.length;

      await act(async () => failJoin!(new Error("Invocation canceled")));
      await flush();
      expect(stopMock).toHaveBeenCalledTimes(1);
      expect(hook.result.current.connectionState).not.toBe("connected");

      // What the library does when stop() completes on a started connection.
      await closeHub();
      await act(async () => {
        await vi.advanceTimersByTimeAsync(30_000);
      });
      await flush();

      expect(startMock.mock.calls.length).toBe(starts + 1);
      expect(hook.result.current.connectionState).toBe("connected");
      hook.unmount();
    },
  );
});
