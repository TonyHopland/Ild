namespace ILD.Data.DTOs;

/// <summary>
/// Request body for <c>POST /api/v1/agent/chat/repositories/{repositoryId}/clone</c>:
/// the branch to clone or switch to, the repository's default branch when omitted.
/// </summary>
public class AgentChatRepositoryCloneRequest
{
    public string? Branch { get; set; }
}
