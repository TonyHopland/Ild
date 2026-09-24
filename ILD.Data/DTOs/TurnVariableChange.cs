namespace ILD.Data.DTOs;

/// <summary>
/// What one node execution did to one loop variable, reduced from its writes:
/// the value it left, whether it created or changed the variable, and whether a
/// later execution of the same run changed it again. An execution that left a
/// variable as it found it has no entry.
/// </summary>
public sealed record TurnVariableChange(
    Guid RunId,
    Guid RunNodeId,
    string Name,
    string Value,
    bool Created,
    bool ChangedLater);
