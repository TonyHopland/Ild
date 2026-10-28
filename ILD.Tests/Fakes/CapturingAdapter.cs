using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;

namespace ILD.Tests.Fakes;

/// <summary>
/// An AI node's agent: records every call it is handed and answers "done" on the
/// session the call resumed, or on <c>newSessionId</c> when it resumed none.
/// </summary>
/// <param name="providerType">The provider type it serves.</param>
/// <param name="newSessionId">The session a call that resumed none ends on.</param>
/// <param name="reportedSessionId">
/// A session it reports mid-stream, as a real CLI does, and ends every call on.
/// </param>
internal sealed class CapturingAdapter(
    string providerType = "claude-code", string? newSessionId = null, string? reportedSessionId = null) : IAgentAdapter
{
    public List<AgentExecutionContext> Calls { get; } = new();

    /// <summary>The latest call, or null before the first.</summary>
    public AgentExecutionContext? Captured => Calls.LastOrDefault();

    public string Name => "Capturing";
    public string[] SupportedProviderTypes => [providerType];
    public ConfigFieldDescriptor[] ConfigSchema => [];
    public AdapterModelSupport ModelSupport => AdapterModelSupport.Unsupported;
    public AgentAdapterTraits Traits => AgentAdapterTraits.None;

    public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
    {
        Calls.Add(context);
        if (reportedSessionId is not null) context.OnSessionId?.Invoke(reportedSessionId);
        return Task.FromResult(NodeExecutionResult.Ok(
            "done", context.Prompt, reportedSessionId ?? context.SessionId ?? newSessionId));
    }
}
