using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>
/// The tools of an external-agent grant with control of work (ADR-0039): start a work item, queue it, reorder it and answer a
/// human-feedback stop. They are offered to such a grant only; the agent API refuses everyone else.
/// </summary>
[McpServerToolType]
[GrantControl]
public sealed class WorkItemControlTools
{
    private readonly IldClient _ild;

    public WorkItemControlTools(IldClient ild) { _ild = ild; }

    private static string Route(string workItemId, string action)
        => $"api/v1/agent/workitems/{Uri.EscapeDataString(workItemId)}/{action}";

    [McpServerTool(Name = "start_workitem")]
    [Description("Start a Ready work item now instead of waiting for the scheduler, as the operator's Start button does. Works on any work item, not only ones you created. Starting spends AI provider budget. Refused when the item is not Ready or ILD is already running as many items as it is allowed to. Returns the work item.")]
    public Task<string> StartWorkItem([Description("The work item to start.")] string workItemId)
        => _ild.PostJsonAsync(Route(workItemId, "start"), new { });

    [McpServerTool(Name = "queue_workitem")]
    [Description("Move a work item into the work queue, where the scheduler picks it up when it is ready to run. Works on any work item. Queueing keeps the item's place: a new item sits above every other one, so items queued in creation order run newest first; use reorder_workitem to set the order. The lowest priority runs first. No other status change is available.")]
    public Task<string> QueueWorkItem([Description("The work item to queue.")] string workItemId)
        => _ild.PostJsonAsync(Route(workItemId, "queue"), new { });

    [McpServerTool(Name = "reorder_workitem")]
    [Description("Place a work item directly after aboveId or, without it, directly before belowId in the board order, as the operator's drag on the board does. The lowest priority runs first, so a higher place runs sooner. Works on any work item, within the item's current status: it never changes the status. Give at least one of aboveId and belowId; each must be another work item. Returns the work item.")]
    public Task<string> ReorderWorkItem(
        [Description("The work item to move.")] string workItemId,
        [Description("The work item it goes directly after (it then runs after that one).")] string? aboveId = null,
        [Description("The work item it goes directly before (it then runs before that one).")] string? belowId = null)
        => _ild.PostJsonAsync(Route(workItemId, "reorder"), new { aboveId, belowId });

    [McpServerTool(Name = "human_feedback_input")]
    [Description("Answer a work item parked in Human Feedback by approving: the run continues along its success edge with your optional text as the node's input. The run is named by runId (get_workitem shows the current run). Refused when that run is not the item's current run or is not waiting for an answer.")]
    public Task<string> HumanFeedbackInput(
        [Description("The work item waiting for an answer.")] string workItemId,
        [Description("The run the item is parked on.")] string runId,
        [Description("Optional text for the next step (up to 8192 chars).")] string? input = null)
        => _ild.PostJsonAsync(Route(workItemId, "human-feedback/input"), new { runId, input });

    [McpServerTool(Name = "human_feedback_respond")]
    [Description("Answer a work item parked in Human Feedback with the Respond output: your text goes back to the agent that asked. The run is named by runId. Refused like human_feedback_input.")]
    public Task<string> HumanFeedbackRespond(
        [Description("The work item waiting for an answer.")] string workItemId,
        [Description("The run the item is parked on.")] string runId,
        [Description("What to tell the agent (up to 8192 chars).")] string input)
        => _ild.PostJsonAsync(Route(workItemId, "human-feedback/respond"), new { runId, input });

    [McpServerTool(Name = "human_feedback_reject")]
    [Description("Answer a work item parked in Human Feedback by rejecting: the run continues along its failure edge with your optional reason. The run is named by runId. Refused like human_feedback_input.")]
    public Task<string> HumanFeedbackReject(
        [Description("The work item waiting for an answer.")] string workItemId,
        [Description("The run the item is parked on.")] string runId,
        [Description("Optional reason (up to 8192 chars).")] string? input = null)
        => _ild.PostJsonAsync(Route(workItemId, "human-feedback/reject"), new { runId, input });

    [McpServerTool(Name = "human_feedback_edge")]
    [Description("Answer a work item parked in Human Feedback by taking one of the parked node's named outputs (its buttons). The run is named by runId. Refused like human_feedback_input, and when the node has no such output.")]
    public Task<string> HumanFeedbackEdge(
        [Description("The work item waiting for an answer.")] string workItemId,
        [Description("The run the item is parked on.")] string runId,
        [Description("The name of the output to take.")] string name,
        [Description("Optional text for the output's successor (up to 8192 chars).")] string? input = null)
        => _ild.PostJsonAsync(Route(workItemId, "human-feedback/edge"), new { runId, name, input });
}
