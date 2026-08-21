using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Contracts;

/// <summary>
/// The single HTTP shaping of a <see cref="PullBranchResult"/>, shared by the human
/// surface (<c>WorkItemsController</c>) and the agent surface (<c>AgentController</c>)
/// so the same pull can never be reported two different ways.
/// </summary>
public static class PullBranchHttpResult
{
    /// <summary>
    /// 200 with the full outcome whenever the pull actually ran — including the
    /// outcomes the caller must act on (<see cref="PullBranchOutcome.DirtyWorktree"/>,
    /// <see cref="PullBranchOutcome.Conflict"/>): those are answers, not malformed
    /// requests, and an agent that receives them as an HTTP error gets an exception
    /// where it needs a decision. Only <see cref="PullBranchOutcome.Failed"/> — no
    /// worktree, no repository, a fetch that could not complete — is a 400, matching
    /// the push-branch endpoint's shape.
    /// </summary>
    public static IActionResult ToActionResult(PullBranchResult result)
    {
        var body = new
        {
            outcome = result.Outcome.ToString(),
            success = result.Success,
            branch = result.Branch,
            message = result.Message,
            files = result.Files,
            // The divergence-vs-base axis: null throughout when the comparison was
            // not made, so a client can tell "in sync" (0) from "not measured".
            baseBranch = result.BaseBranch,
            behindBase = result.BehindBase,
            aheadOfBase = result.AheadOfBase,
        };

        return result.Outcome == PullBranchOutcome.Failed
            ? new BadRequestObjectResult(new { error = result.Message, body.outcome, body.branch, body.files })
            : new OkObjectResult(body);
    }
}
