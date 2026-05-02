import { afterEach, describe, expect, test, vi } from "vite-plus/test";
import { renderHook, act } from "@testing-library/react";
import { useMediaQuery } from "./useMediaQuery";

let mockMatches = false;
const mockListeners: Array<(mq: MediaQueryListEvent) => void> = [];

let lastQuery = "";

vi.stubGlobal(
  "matchMedia",
  vi.fn((query: string) => {
    lastQuery = query;
    return {
      matches: mockMatches,
      media: query,
      addEventListener: vi.fn((_type: string, listener: (mq: MediaQueryListEvent) => void) => {
        mockListeners.push(listener);
      }),
      removeEventListener: vi.fn(() => {}),
      addListener: vi.fn(() => {}),
      removeListener: vi.fn(() => {}),
    };
  }),
);

function fireMediaChange(matches: boolean) {
  const mockEvent = { matches, media: lastQuery };
  for (const listener of mockListeners) {
    listener(mockEvent as MediaQueryListEvent);
  }
}

afterEach(() => {
  mockMatches = false;
  mockListeners.length = 0;
});

describe("useMediaQuery", () => {
  test("returns current match state on mount", () => {
    mockMatches = true;
    const { result } = renderHook(() => useMediaQuery("(max-width: 768px)"));
    expect(result.current).toBe(true);
  });

  test("returns false when query does not match", () => {
    mockMatches = false;
    const { result } = renderHook(() => useMediaQuery("(max-width: 768px)"));
    expect(result.current).toBe(false);
  });

  test("updates when media query changes", () => {
    mockMatches = false;
    const { result } = renderHook(() => useMediaQuery("(max-width: 768px)"));
    expect(result.current).toBe(false);

    act(() => {
      fireMediaChange(true);
    });
    expect(result.current).toBe(true);

    act(() => {
      fireMediaChange(false);
    });
    expect(result.current).toBe(false);
  });

  test("stores the queried media string", () => {
    renderHook(() => useMediaQuery("(min-width: 1024px)"));
    expect(lastQuery).toBe("(min-width: 1024px)");
  });
});
