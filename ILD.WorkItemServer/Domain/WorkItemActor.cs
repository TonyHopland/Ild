namespace ILD.WorkItemServer.Domain;

/// <summary>
/// Who a request acts for, as its X-ILD-Actor header names it. Opaque text to the
/// server: it is recorded on every history row the request writes, never checked
/// against anything.
/// </summary>
/// <param name="Detail">What acted on the user's behalf: a loop run's step, a chat, a system process.</param>
/// <param name="WorkItemId">The work item the acting loop run belongs to.</param>
public sealed record WorkItemActor(string Username, WorkItemActorKind Kind, string? Detail, string? WorkItemId);
