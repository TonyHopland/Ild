import { afterEach, beforeEach, describe, expect, test, vi } from "vite-plus/test";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import EventLogViewer from "./EventLogViewer";
import * as loopRunServiceModule from "../services/auth";

const mockEvents = [
  {
    sequence: 1,
    runId: "test-run",
    eventType: "NodeStarted",
    nodeId: null,
    payload: "First event message",
    timestamp: "2025-01-01T00:00:00Z",
    hasPayload: false,
  },
  {
    sequence: 2,
    runId: "test-run",
    eventType: "NodeCompleted",
    nodeId: "node-1",
    payload: "Second event message",
    timestamp: "2025-01-01T00:01:00Z",
    hasPayload: true,
  },
];

const mockPage = {
  entries: mockEvents,
  nextCursor: 2,
  hasMore: true,
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

beforeEach(() => {
  vi.spyOn(loopRunServiceModule.loopRunService, "getEvents").mockResolvedValue(mockPage);
  vi.spyOn(loopRunServiceModule.loopRunService, "getPayload").mockResolvedValue({
    payload: "large payload content",
  });
});

describe("EventLogViewer", () => {
  test("renders event list with sequence, type badge, timestamp, and message", async () => {
    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run/events"]}>
        <EventLogViewer />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("#1")).toBeTruthy();
    });

    expect(screen.getByText("NodeStarted")).toBeTruthy();
    expect(screen.getByText("NodeCompleted")).toBeTruthy();
    expect(screen.getByText("First event message")).toBeTruthy();
  });

  test("expands event to show full payload on click", async () => {
    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run/events"]}>
        <EventLogViewer />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("#1")).toBeTruthy();
    });

    const eventItem = screen.getByText("#1").closest(".event-item");
    fireEvent.click(eventItem!);

    expect(eventItem?.classList.contains("expanded")).toBe(true);
  });

  test("shows Load More button when hasMore is true", async () => {
    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run/events"]}>
        <EventLogViewer />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Load More")).toBeTruthy();
    });
  });

  test("fetches next page when Load More is clicked", async () => {
    const mockPage2 = {
      entries: [
        {
          sequence: 3,
          runId: "test-run",
          eventType: "NodeStarted",
          nodeId: null,
          payload: "Third event",
          timestamp: "2025-01-01T00:02:00Z",
          hasPayload: false,
        },
      ],
      nextCursor: 3,
      hasMore: false,
    };

    vi.spyOn(loopRunServiceModule.loopRunService, "getEvents")
      .mockResolvedValueOnce(mockPage)
      .mockResolvedValueOnce(mockPage2);

    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run/events"]}>
        <EventLogViewer />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("Load More")).toBeTruthy();
    });

    fireEvent.click(screen.getByText("Load More"));

    await waitFor(() => {
      expect(screen.getByText("Third event")).toBeTruthy();
    });
  });

  test("shows Load Payload button for events with disk-stored payloads", async () => {
    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run/events"]}>
        <EventLogViewer />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("#2")).toBeTruthy();
    });

    const eventItem = screen.getByText("#2").closest(".event-item");
    fireEvent.click(eventItem!);

    expect(screen.getByText("Load Payload")).toBeTruthy();
  });

  test("loads and displays payload when Load Payload is clicked", async () => {
    render(
      <MemoryRouter initialEntries={["/loop-runs/test-run/events"]}>
        <EventLogViewer />
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByText("#2")).toBeTruthy();
    });

    const eventItem = screen.getByText("#2").closest(".event-item");
    fireEvent.click(eventItem!);

    fireEvent.click(screen.getByText("Load Payload"));

    await waitFor(() => {
      expect(screen.getByText("large payload content")).toBeTruthy();
    });
  });
});
