namespace ILD.Data.Enums;

/// <summary>
/// Which events make up a run's conversation: what the conversation projection
/// reads, and what may no longer be appended once the run has ended. Everything
/// else in the event log is housekeeping and may still follow the end.
/// </summary>
public static class RunConversationEvents
{
    /// <summary>The events that close a run's conversation.</summary>
    public static readonly IReadOnlySet<EventType> Ending = new HashSet<EventType>
    {
        EventType.LoopRunCompleted,
        EventType.LoopRunFailed,
        EventType.LoopRunCancelled,
    };

    /// <summary>
    /// Run-level and park events, shown in the conversation as system messages.
    /// A feedback request is not one: the feedback card already shows its prompt.
    /// </summary>
    public static readonly IReadOnlySet<EventType> System = new HashSet<EventType>(Ending)
    {
        EventType.LoopRunStarted,
        EventType.RunParked,
        EventType.RecoveryTriggered,
    };

    /// <summary>Every event type the conversation projection can turn into a message.</summary>
    public static readonly IReadOnlySet<EventType> Projected = new HashSet<EventType>(System)
    {
        EventType.NodeCompleted,
        EventType.HumanFeedbackReceived,
    };

    /// <summary>
    /// Whether an event of <paramref name="type"/> belongs to the conversation.
    /// A completed node is an AI turn only when the node is an AI node.
    /// </summary>
    public static bool Contains(EventType type, bool fromAiNode)
        => type switch
        {
            EventType.NodeCompleted => fromAiNode,
            EventType.HumanFeedbackReceived => true,
            _ => System.Contains(type),
        };
}
