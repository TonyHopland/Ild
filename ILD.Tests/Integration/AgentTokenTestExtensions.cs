using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

public static class AgentTokenTestExtensions
{
    /// <summary>The principal the authentication handler builds for a live agent token, for controllers driven without the pipeline.</summary>
    public static System.Security.Claims.ClaimsPrincipal AgentPrincipal(AgentTokenSource source, Guid sourceId)
        => new(new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, ILD.Api.Authentication.IldAuthentication.AgentRole),
                new System.Security.Claims.Claim(ILD.Api.Authentication.IldAuthentication.SessionSourceClaim, source.ToString().ToLowerInvariant()),
                new System.Security.Claims.Claim(ILD.Api.Authentication.IldAuthentication.SessionIdClaim, sourceId.ToString()),
            ],
            "ILD", System.Security.Claims.ClaimTypes.Name, System.Security.Claims.ClaimTypes.Role));

    /// <summary>Makes the request come from the run's agent: its own token, in place of whatever the client signed in with.</summary>
    public static void ActAsRun(this HttpRequestMessage request, IServiceProvider services, Guid runId)
        => request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", services.IssueAgentToken(AgentTokenSource.Run, runId));

    public static void ActAsChat(this HttpRequestMessage request, IServiceProvider services, Guid chatSessionId)
        => request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", services.IssueAgentToken(AgentTokenSource.Chat, chatSessionId));

    /// <summary>
    /// A live agent token for a run or chat. With no id it belongs to a chat that does not exist, which is an agent
    /// with no run behind it.
    /// </summary>
    /// <summary>A token for the run when there is one, else for the chat, else for an agent with neither.</summary>
    public static string IssueAgentToken(this IServiceProvider services, Guid? runId, Guid? chatSessionId)
        => runId is { } run ? services.IssueAgentToken(AgentTokenSource.Run, run)
            : chatSessionId is { } chat ? services.IssueAgentToken(AgentTokenSource.Chat, chat)
            : services.IssueAgentToken();

    public static string IssueAgentToken(this IServiceProvider services, AgentTokenSource source = AgentTokenSource.Chat, Guid? sourceId = null)
    {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAgentTokenService>()
            .IssueAsync(source, sourceId ?? Guid.NewGuid()).GetAwaiter().GetResult().Token;
    }
}
