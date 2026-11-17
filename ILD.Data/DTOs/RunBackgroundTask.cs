namespace ILD.Data.DTOs;

/// <summary>
/// One piece of background work the agent CLI is waiting for before it ends a launch, as the CLI itself reports it
/// (Claude Code's <c>background_tasks_changed</c>): the kind (<c>local_bash</c>, <c>local_agent</c>, a monitor …), what the agent
/// called it, and for a subagent its type. ILD only shows these; it keeps no list of its own.
/// </summary>
public sealed record RunBackgroundTask(string Kind, string Description, string? SubagentType = null);
