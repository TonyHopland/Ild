using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// MCP tool for reading a pull request's CI logs. The PR node hands a fix-it
/// agent a failure reason naming each failing check and its id; this is how the
/// agent gets from that summary to the actual error, without the forge
/// credentials being anywhere near it — the server does the authenticated fetch.
///
/// Drift warning: this name and shape must stay in lockstep with the Pi surface
/// (<see cref="ILD.Data.ToolDescriptors"/>) and the agent-API endpoint
/// (<c>AgentController</c>) so the behaviour is the same whichever CLI backs it.
/// </summary>
[McpServerToolType]
public sealed class CiTools
{
    private readonly IldClient _ild;

    public CiTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_ci_log")]
    [Description("Read the tail of a failing CI check's log for a work item's pull request. Use when a CI failure reason names a check and its summary is not enough to fix it — the check id comes from that reason. The log is fetched server-side with the forge credentials. Returns {available, text, lines, offset, totalLines, truncated, message}: the end of the log comes first to hand, so raise offset to walk backwards when the error is above the window, and treat truncated=true as 'there is more, ask again' rather than 'that was all'. available=false with a message means the provider has no log to fetch (its CI lives outside the forge) — read the message and use the URL in it.")]
    public Task<string> GetCiLog(
        [Description("Work item GUID (from the Chat Context, or the CI failure reason).")] string workItemId,
        [Description("Id of the failing check, as named in the CI failure reason.")] string checkId,
        [Description("How many lines to return, counting back from the end (default 200, max 2000).")] int tailLines = 200,
        [Description("Lines to skip from the end before taking the window — raise it to walk backwards through the log (default 0).")] int offset = 0)
        => _ild.GetRawAsync(
            $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/ci-log"
            + $"?checkId={Uri.EscapeDataString(checkId)}&tailLines={tailLines}&offset={offset}");
}
