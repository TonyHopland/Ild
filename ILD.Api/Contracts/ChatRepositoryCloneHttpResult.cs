using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Contracts;

/// <summary>
/// The HTTP shaping of a <see cref="ChatRepositoryCloneResult"/>. A refusal is an
/// error status, so the MCP server hands it to the agent as a tool error carrying
/// the reason.
/// </summary>
public static class ChatRepositoryCloneHttpResult
{
    public static IActionResult ToActionResult(ChatRepositoryCloneResult result)
    {
        if (result.Success)
        {
            return new OkObjectResult(new
            {
                outcome = result.Outcome.ToString(),
                path = result.Path,
                branch = result.Branch,
                commit = result.Commit,
                previousCommit = result.PreviousCommit,
                changed = result.Changed,
                message = result.Message,
            });
        }

        var status = result.Outcome switch
        {
            ChatRepositoryCloneOutcome.UnknownChat
                or ChatRepositoryCloneOutcome.NotScheduledChat
                or ChatRepositoryCloneOutcome.NotAllowed => StatusCodes.Status403Forbidden,
            ChatRepositoryCloneOutcome.UnknownRepository
                or ChatRepositoryCloneOutcome.BranchNotFound => StatusCodes.Status404NotFound,
            ChatRepositoryCloneOutcome.InvalidBranch => StatusCodes.Status400BadRequest,
            ChatRepositoryCloneOutcome.UpdateRefused => StatusCodes.Status409Conflict,
            ChatRepositoryCloneOutcome.FetchFailed => StatusCodes.Status502BadGateway,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "not a refusal"),
        };
        return new ObjectResult(new { error = result.Message, outcome = result.Outcome.ToString() }) { StatusCode = status };
    }
}
