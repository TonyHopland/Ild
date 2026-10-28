using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests.Fakes;

/// <summary>
/// Records every turn it is handed and binds a session on the first one, as a
/// real CLI does, so a later turn is a resume rather than a fresh start.
/// </summary>
internal sealed class RecordingChatAdapter : IAgentAdapter
{
    public List<AgentExecutionContext> Contexts { get; } = new();
    public List<string> Prompts => Contexts.Select(c => c.Prompt).ToList();

    /// <summary>
    /// Makes this and every later turn bind a different agent session, as a fork
    /// or a snapshot that could not be restored would.
    /// </summary>
    public string? NextSessionId { get; set; }

    /// <summary>
    /// Makes the next turn throw the way an adapter does when the CLI never
    /// launches — before any session is bound, so nothing this turn was handed
    /// can have reached an agent. Cleared once it has fired.
    /// </summary>
    public bool FailNextTurnBeforeLaunch { get; set; }

    public string Name => "fake";
    public string[] SupportedProviderTypes => ["fake"];
    public ConfigFieldDescriptor[] ConfigSchema => [];
    public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
    public AgentAdapterTraits Traits => AgentAdapterTraits.None;

    public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
    {
        Contexts.Add(context);
        if (FailNextTurnBeforeLaunch)
        {
            FailNextTurnBeforeLaunch = false;
            throw new InvalidOperationException("claude: command not found");
        }
        return Task.FromResult(
            NodeExecutionResult.Ok("ok", context.Prompt, NextSessionId ?? context.SessionId ?? "sess-1"));
    }
}
