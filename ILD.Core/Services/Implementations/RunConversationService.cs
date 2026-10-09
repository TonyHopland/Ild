using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;

namespace ILD.Core.Services.Implementations;

public sealed class RunConversationService : IRunConversationService
{
    private readonly IEventLogStore _events;
    private readonly ILoopRunStore _runs;

    public RunConversationService(IEventLogStore events, ILoopRunStore runs)
    {
        _events = events;
        _runs = runs;
    }

    public async Task<IReadOnlyList<RunConversationMessage>?> GetMessagesAsync(Guid runId)
    {
        if (await _runs.GetByIdAsync(runId) is null) return null;
        return await ProjectAsync(runId, await _events.GetByRunIdAsync(runId));
    }

    public async Task<IReadOnlyList<RunConversationMessage>> ProjectAsync(Guid runId, IReadOnlyList<EventLog> events)
    {
        // Only an AI turn needs its execution, for the node's type and label.
        var runNodes = events.Any(e => e.EventType == EventType.NodeCompleted)
            ? (await _runs.GetRunNodesWithNodeAsync(runId)).ToDictionary(rn => rn.Id)
            : new Dictionary<Guid, LoopRunNode>();
        var messages = new List<RunConversationMessage>();
        foreach (var e in events)
        {
            var text = e.Data ?? string.Empty;
            var runNode = e.RunNodeId is { } runNodeId ? runNodes.GetValueOrDefault(runNodeId) : null;
            var (role, name) = e.EventType switch
            {
                EventType.NodeCompleted when runNode?.LoopNode?.NodeType == NodeType.AI && text.Length > 0
                    => (RunConversationMessage.Ai, AiName(runNode.NodeLabel, runNode.LoopNode.Label)),
                EventType.HumanFeedbackReceived when text.Length > 0
                    => (RunConversationMessage.Human, "Human"),
                var type when RunConversationEvents.System.Contains(type)
                    => (RunConversationMessage.System, type.ToString()),
                _ => (null, null),
            };
            if (role is not null)
                messages.Add(new RunConversationMessage(e.Id, runId, e.RunNodeId, role, name!, text, e.Timestamp));
        }
        return messages;
    }

    private static string AiName(string? runNodeLabel, string? nodeLabel)
        => !string.IsNullOrEmpty(runNodeLabel) ? runNodeLabel
            : !string.IsNullOrEmpty(nodeLabel) ? nodeLabel
            : "AI";
}
