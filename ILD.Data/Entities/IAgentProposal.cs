namespace ILD.Data.Entities;

/// <summary>
/// A change an agent proposes and a human decides, made by a loop run or by a
/// chat (ADR-0027, ADR-0028).
/// </summary>
public interface IAgentProposal
{
    Guid Id { get; set; }

    /// <summary>Mutually exclusive with <see cref="CreatedByChatSessionId"/>.</summary>
    Guid? CreatedByLoopRunId { get; set; }

    Guid? CreatedByChatSessionId { get; set; }

    string? RejectionReason { get; set; }

    DateTime CreatedAt { get; set; }

    DateTime? DecidedAt { get; set; }

    DateTime? DecisionDeliveredAt { get; set; }
}

public interface IAgentProposal<TStatus> : IAgentProposal
    where TStatus : struct, Enum
{
    TStatus Status { get; set; }
}
