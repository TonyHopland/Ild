using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ILD.McpServer.Tools;

/// <summary>The loop authoring guide, which needs no run or chat: it describes the loop template model itself.</summary>
[McpServerToolType]
public sealed class LoopGuideTools
{
    private readonly IldClient _ild;

    public LoopGuideTools(IldClient ild) { _ild = ild; }

    [McpServerTool(Name = "get_loop_authoring_guide")]
    [Description("Read the loop authoring guide: the node/edge vocabulary, the config field semantics you cannot infer from the name, the graph rules a human's save enforces, and the authoring practices. Call this before writing or restructuring a loop, and again whenever you are unsure of a rule — the chat sends it once per session, so this is how you get it back. Static text; it does not depend on which loop is open.")]
    public Task<string> GetLoopAuthoringGuide()
        => _ild.GetRawAsync("api/v1/agent/loop-authoring-guide");
}
