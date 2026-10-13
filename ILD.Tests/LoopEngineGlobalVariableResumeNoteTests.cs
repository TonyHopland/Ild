using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Enums;

namespace ILD.Tests;

/// <summary>
/// A missing-global park is the one park that can sit before a node other than
/// an AI node. A note given at its Resume has no agent there to steer, so it must
/// not be left on the run for some later AI node to append to its prompt.
/// </summary>
public class LoopEngineGlobalVariableResumeNoteTests
{
    [Fact]
    public async Task A_note_given_when_resuming_before_a_prompt_node_is_kept_in_the_conversation_but_not_carried_to_a_later_AI_node()
    {
        using var h = new LoopEngineHarness();
        h.AddNode("n", NodeType.Prompt);
        h.NodesById["n"].Config = """{"prompt":"{{Global.Rules}}"}""";
        h.AddNode("done", NodeType.Cleanup);
        h.AddEdge("n", "done", EdgeType.OnSuccess);
        h.Db.Context.SaveChanges();
        h.Registry.Register(new ScriptedExecutor(NodeType.Prompt,
            new NodeOutcome.NodeStarting("p"), new NodeOutcome.Success(EdgeType.OnSuccess, "ok")));
        h.Registry.Register(new ScriptedExecutor(NodeType.Cleanup,
            new NodeOutcome.NodeStarting("cleanup"), new NodeOutcome.Terminal("done")));
        h.SeedRun("n");
        await h.RunAsync();
        Assert.Equal(HaltReason.GlobalVariableMissing, h.ReloadRun().HaltReason);

        h.Db.Context.Set<GlobalVariable>().Add(new GlobalVariable
        {
            Id = Guid.NewGuid(), Name = "Rules", NormalizedName = "RULES", Value = "be brief", UpdatedAt = DateTime.UtcNow,
        });
        h.Db.Context.SaveChanges();
        await h.Engine.ResumeFromHaltAsync(h.RunId, "use the short form");
        await h.WaitUntilIdleAsync();

        var run = h.ReloadRun();
        Assert.Equal(LoopRunStatus.Completed, run.Status);
        Assert.Null(run.SteeringNote);
        Assert.Contains(h.ReloadEvents(), e => e.EventType == EventType.HumanFeedbackReceived && e.Data == "use the short form");
    }
}
