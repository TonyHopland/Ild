namespace ILD.Data.Enums;

/// <summary>
/// The routing role of a <see cref="Entities.LoopNodeEdge"/>. An edge's identity
/// is the pair (role, <see cref="Entities.LoopNodeEdge.Name"/>):
/// <list type="bullet">
/// <item><see cref="OnSuccess"/> — the node's default outlet (Name is null).</item>
/// <item><see cref="OnFailure"/> — the node's error fallback (Name is null).</item>
/// <item><see cref="Custom"/> — a named custom outlet (Name is set); only Human,
/// AI and PR nodes may declare these, and a node may declare any number of them
/// as long as their names are unique.</item>
/// </list>
/// </summary>
public enum EdgeType
{
    OnSuccess = 0,
    OnFailure = 1,

    // Value 2 was historically "OnRespond"; it is now the generic named-custom
    // role. Existing OnRespond rows migrate to Custom with Name="Respond".
    Custom = 2,
}
