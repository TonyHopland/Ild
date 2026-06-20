// Typed SignalR event payloads. Mirrors `ILD.Data.DTOs.SignalRPayloads` and the
// hub event names in `ILD.Api/Hubs/`.
//
// To add a new event:
//   1. Add the payload interface here (or import from ../types).
//   2. Add a key/value pair to `SignalREventPayloads`.
//   3. Consumers can call `on("YourEvent", handler)` and `handler` will be
//      typed against the payload automatically — no `as` casts needed.

import type {
  NodeStateChangedPayload,
  LoopRunStateChangedPayload,
  WorkItemStateChangedPayload,
  HumanFeedbackRequiredPayload,
  EventLoggedPayload,
  RunPausedPayload,
  RunResumedPayload,
  RunHaltedPayload,
  DependencyResolvedPayload,
  NodeProgressPayload,
  PrSnapshotChangedPayload,
  PreviewStateChangedPayload,
  WorkItemRunProgressedPayload,
  SchedulerStateChangedPayload,
  ChatMessageAppendedPayload,
  ChatTurnProgressPayload,
  ChatTurnCompletedPayload,
} from "./index";

export interface SignalREventPayloads {
  NodeStateChanged: NodeStateChangedPayload;
  LoopRunStateChanged: LoopRunStateChangedPayload;
  WorkItemStateChanged: WorkItemStateChangedPayload;
  HumanFeedbackRequired: HumanFeedbackRequiredPayload;
  EventLogged: EventLoggedPayload;
  RunPaused: RunPausedPayload;
  RunResumed: RunResumedPayload;
  RunHalted: RunHaltedPayload;
  DependencyResolved: DependencyResolvedPayload;
  NodeProgress: NodeProgressPayload;
  PrSnapshotChanged: PrSnapshotChangedPayload;
  PreviewStateChanged: PreviewStateChangedPayload;
  WorkItemRunProgressed: WorkItemRunProgressedPayload;
  SchedulerStateChanged: SchedulerStateChangedPayload;
  ChatMessageAppended: ChatMessageAppendedPayload;
  ChatTurnProgress: ChatTurnProgressPayload;
  ChatTurnCompleted: ChatTurnCompletedPayload;
}

export type SignalREventName = keyof SignalREventPayloads;

// A SignalR message whose `payload` is typed against the event name.
// Falls back to `unknown` for unknown event names (e.g. test fixtures).
export type TypedSignalRMessage<E extends string = string> = {
  type: E;
  payload: E extends SignalREventName ? SignalREventPayloads[E] : unknown;
  timestamp: string;
};
