using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The WorkItem server no longer keeps a conversation or takes feedback: the
/// run's event log on the ILD instance is the only timeline, and an answer goes
/// to the run that asked for it.
/// </summary>
public sealed class WorkItemServerConversationRemovalTests : IClassFixture<WorkItemServerApiTests.Factory>
{
    private readonly WorkItemServerApiTests.Factory _factory;
    public WorkItemServerConversationRemovalTests(WorkItemServerApiTests.Factory factory) => _factory = factory;

    [Fact]
    public async Task There_is_no_conversation_or_feedback_endpoint_and_a_feedback_call_moves_nothing()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkItemServerApiTests.Factory.ApiKey);
        var ct = TestContext.Current.CancellationToken;
        var create = await c.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "no thread" }, cancellationToken: ct);
        var id = (await create.Content.ReadFromJsonAsync<WorkItemDto>(ct))!.Id;

        var append = await c.PostAsJsonAsync($"/workitems/{id}/conversation",
            new { role = "ai", content = "a turn", name = "Coder" }, cancellationToken: ct);
        Assert.Contains(append.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        (await c.PostAsJsonAsync($"/workitems/{id}/transition",
            new TransitionRequest { TargetStatus = WorkItemStatus.HumanFeedback }, cancellationToken: ct)).EnsureSuccessStatusCode();
        var feedback = await c.PostAsJsonAsync($"/workitems/{id}/feedback", new { content = "Looks good" }, cancellationToken: ct);
        Assert.Contains(feedback.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });

        using var item = JsonDocument.Parse(await c.GetStringAsync($"/workitems/{id}", ct));
        Assert.DoesNotContain(item.RootElement.EnumerateObject(),
            p => p.Name.Equals("conversation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Looks good", item.RootElement.GetRawText());
        Assert.Equal(WorkItemStatus.HumanFeedback, (await c.GetFromJsonAsync<WorkItemDto>($"/workitems/{id}", ct))!.Status);
    }
}
