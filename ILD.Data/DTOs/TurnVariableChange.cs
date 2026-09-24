namespace ILD.Data.DTOs;

/// <summary>
/// What one node execution did to one loop variable, reduced from its writes:
/// the value it left, whether it created or changed the variable, and whether
/// the value it left was later overwritten in the same run. An execution that
/// left a variable as it found it has no entry.
/// </summary>
/// <param name="ChangedLater">
/// A later write in the run set a different value, whoever made it — a write
/// no execution is credited with still means the value shown is no longer the
/// variable's value.
/// </param>
public sealed record TurnVariableChange(
    Guid RunId,
    Guid RunNodeId,
    string Name,
    string Value,
    bool Created,
    bool ChangedLater);
