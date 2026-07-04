namespace ILD.WorkItemServer.Domain;

/// <summary>
/// How a work item overrides the AI provider its AI nodes run against.
/// <see cref="None"/> leaves each node's own provider choice untouched;
/// <see cref="OverrideDefault"/> only replaces nodes that fell back to the
/// configured default provider (a node pinned to a specific provider in the
/// loop is left alone); <see cref="OverrideAll"/> replaces every AI node's
/// provider regardless of what the loop pinned.
/// </summary>
public enum AiProviderOverrideMode
{
    None = 0,
    OverrideDefault = 1,
    OverrideAll = 2,
}
