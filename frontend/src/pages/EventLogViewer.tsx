import { useState, useEffect, useCallback } from "react";
import { useParams } from "react-router-dom";
import { EventLogEntry } from "../types";
import { loopRunService } from "../services/auth";

const eventTypeColors: Record<string, string> = {
  NodeStarted: "#3b82f6",
  NodeCompleted: "#22c55e",
  NodeFailed: "#ef4444",
  EdgeTraversed: "#a855f7",
  LoopRunStarted: "#3b82f6",
  LoopRunCompleted: "#22c55e",
  LoopRunFailed: "#ef4444",
  LoopRunCancelled: "#6b7280",
  HumanFeedbackRequested: "#f59e0b",
  HumanFeedbackReceived: "#22c55e",
  RecoveryTriggered: "#f59e0b",
  Error: "#ef4444",
};

export default function EventLogViewer() {
  const { runId } = useParams<{ runId: string }>();
  const [events, setEvents] = useState<EventLogEntry[]>([]);
  const [cursor, setCursor] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [expandedEvents, setExpandedEvents] = useState<Set<number>>(new Set());
  const [loadingPayloads, setLoadingPayloads] = useState<Set<number>>(new Set());
  const [loadedPayloads, setLoadedPayloads] = useState<Record<number, string>>({});

  const fetchEvents = useCallback(
    async (cur: number) => {
      try {
        const page = await loopRunService.getEvents(runId!, cur, 50);
        if (cur === 0) {
          setEvents(page.entries);
        } else {
          setEvents((prev) => [...prev, ...page.entries]);
        }
        setCursor(page.nextCursor);
        setHasMore(page.hasMore);
      } catch (error) {
        console.error("Failed to load events:", error);
      } finally {
        setIsLoading(false);
        setLoadingMore(false);
      }
    },
    [runId],
  );

  useEffect(() => {
    void fetchEvents(0);
  }, [fetchEvents]);

  const handleLoadMore = () => {
    setLoadingMore(true);
    void fetchEvents(cursor);
  };

  const toggleExpand = (sequence: number) => {
    setExpandedEvents((prev) => {
      const next = new Set(prev);
      if (next.has(sequence)) {
        next.delete(sequence);
      } else {
        next.add(sequence);
      }
      return next;
    });
  };

  const handleLoadPayload = async (sequence: number) => {
    setLoadingPayloads((prev) => new Set(prev).add(sequence));
    try {
      const { payload } = await loopRunService.getPayload(runId!, sequence);
      setLoadedPayloads((prev) => ({ ...prev, [sequence]: payload }));
    } catch (error) {
      console.error("Failed to load payload:", error);
    } finally {
      setLoadingPayloads((prev) => {
        const next = new Set(prev);
        next.delete(sequence);
        return next;
      });
    }
  };

  const formatTimestamp = (ts: string) => {
    return new Date(ts).toLocaleString();
  };

  const truncate = (text: string, maxLen: number) => {
    if (!text) return "";
    return text.length > maxLen ? text.slice(0, maxLen) + "..." : text;
  };

  if (isLoading && events.length === 0) {
    return (
      <div className="page-container">
        <p>Loading event log...</p>
      </div>
    );
  }

  return (
    <div className="page-container">
      <div className="event-log-header">
        <h1 className="page-title">Event Log</h1>
        <span className="event-count">{events.length} events</span>
      </div>
      <div className="event-list">
        {events.map((event) => {
          const isExpanded = expandedEvents.has(event.sequence);
          const isPayloadLoading = loadingPayloads.has(event.sequence);
          const loadedPayload = loadedPayloads[event.sequence];

          return (
            <div
              key={event.sequence}
              className={`event-item ${isExpanded ? "expanded" : ""}`}
              onClick={() => toggleExpand(event.sequence)}
            >
              <div className="event-summary">
                <span className="event-sequence">#{event.sequence}</span>
                <span
                  className="event-type-badge"
                  style={{ backgroundColor: eventTypeColors[event.eventType] || "#6b7280" }}
                >
                  {event.eventType}
                </span>
                <span className="event-timestamp">{formatTimestamp(event.timestamp)}</span>
                <span className="event-message">{truncate(event.payload, 120)}</span>
              </div>
              {isExpanded && (
                <div className="event-detail">
                  {event.payload && !event.hasPayload && (
                    <pre className="event-payload">{event.payload}</pre>
                  )}
                  {event.hasPayload && (
                    <div className="event-payload-actions">
                      {loadedPayload ? (
                        <pre className="event-payload">{loadedPayload}</pre>
                      ) : (
                        <button
                          className="btn btn-small btn-secondary load-payload-btn"
                          onClick={(e) => {
                            e.stopPropagation();
                            void handleLoadPayload(event.sequence);
                          }}
                          disabled={isPayloadLoading}
                        >
                          {isPayloadLoading ? "Loading..." : "Load Payload"}
                        </button>
                      )}
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
      {hasMore && (
        <div className="load-more-container">
          <button className="btn btn-secondary" onClick={handleLoadMore} disabled={loadingMore}>
            {loadingMore ? "Loading..." : "Load More"}
          </button>
        </div>
      )}
      <style>{`
        .event-log-header {
          display: flex;
          justify-content: space-between;
          align-items: center;
          margin-bottom: 1rem;
        }

        .event-count {
          font-size: 0.8rem;
          color: #707090;
        }

        .event-list {
          display: flex;
          flex-direction: column;
          gap: 0.25rem;
        }

        .event-item {
          background-color: #1e1e30;
          border-radius: 0.5rem;
          border: 1px solid #2d2d44;
          cursor: pointer;
          overflow: hidden;
          transition: border-color 0.15s;
        }

        .event-item:hover {
          border-color: #4a4a6a;
        }

        .event-item.expanded {
          border-color: #5a5a8a;
        }

        .event-summary {
          display: flex;
          align-items: center;
          gap: 0.75rem;
          padding: 0.75rem 1rem;
          font-size: 0.8rem;
        }

        .event-sequence {
          color: #707090;
          font-size: 0.75rem;
          min-width: 2.5rem;
        }

        .event-type-badge {
          font-size: 0.65rem;
          padding: 0.125rem 0.5rem;
          border-radius: 0.25rem;
          color: #fff;
          text-transform: uppercase;
          letter-spacing: 0.05em;
          min-width: 7rem;
          text-align: center;
        }

        .event-timestamp {
          color: #707090;
          font-size: 0.7rem;
          min-width: 9rem;
        }

        .event-message {
          color: #c0c0d0;
          flex: 1;
          overflow: hidden;
          text-overflow: ellipsis;
          white-space: nowrap;
        }

        .event-detail {
          padding: 0 1rem 0.75rem 1rem;
          border-top: 1px solid #2d2d44;
        }

        .event-payload {
          background-color: #16162a;
          border-radius: 0.25rem;
          padding: 0.75rem;
          margin-top: 0.5rem;
          font-size: 0.75rem;
          color: #a0a0c0;
          overflow-x: auto;
          white-space: pre-wrap;
          word-break: break-word;
          max-height: 300px;
          overflow-y: auto;
        }

        .event-payload-actions {
          margin-top: 0.5rem;
        }

        .load-more-container {
          display: flex;
          justify-content: center;
          padding: 1rem 0;
        }

        .btn-secondary {
          background-color: #2d2d44;
          color: #c0c0d0;
          padding: 0.375rem 1rem;
          border: none;
          border-radius: 0.25rem;
          cursor: pointer;
          font-size: 0.8rem;
        }

        .btn-secondary:hover {
          background-color: #3d3d54;
        }

        .btn-secondary:disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        .load-payload-btn {
          background-color: #1e3a5f;
          color: #60a5fa;
        }

        .load-payload-btn:hover {
          background-color: #2a4a7f;
        }
      `}</style>
    </div>
  );
}
