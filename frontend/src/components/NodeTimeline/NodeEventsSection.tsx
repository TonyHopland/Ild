import { useState } from "react";
import { EventLogEntry } from "../../types";

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

const MAX_CHARS = 120;

interface NodeEventsSectionProps {
  events: EventLogEntry[];
}

export default function NodeEventsSection({ events }: NodeEventsSectionProps) {
  if (events.length === 0) {
    return (
      <div className="node-detail-section node-events-section">
        <h4>Events</h4>
        <div className="node-events-empty">No events</div>
      </div>
    );
  }

  return (
    <div className="node-detail-section node-events-section">
      <h4>Events ({events.length})</h4>
      <div className="node-events-list">
        {events.map((event) => {
          const color = eventTypeColors[event.eventType] ?? "#6b7280";
          const isLong = event.payload.length > MAX_CHARS;
          return <EventItem key={event.sequence} event={event} color={color} isLong={isLong} />;
        })}
      </div>
    </div>
  );
}

interface EventItemProps {
  event: EventLogEntry;
  color: string;
  isLong: boolean;
}

function EventItem({ event, color, isLong }: EventItemProps) {
  const [expanded, setExpanded] = useState(false);

  return (
    <>
      <div className="node-event-item">
        <span className="node-event-badge" style={{ borderColor: color, color }}>
          {event.eventType}
        </span>
        <span className="node-event-message">
          {expanded ? event.payload : event.payload.slice(0, MAX_CHARS) + (isLong ? "..." : "")}
        </span>
        <span className="node-event-time">{new Date(event.timestamp).toLocaleTimeString()}</span>
      </div>
      {isLong && (
        <button className="node-event-toggle" onClick={() => setExpanded(!expanded)}>
          {expanded ? "Show less" : "Show more"}
        </button>
      )}
    </>
  );
}
