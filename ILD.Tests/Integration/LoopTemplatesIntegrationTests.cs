using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Remote;

namespace ILD.Tests.Integration;

public class LoopTemplatesIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/looptemplates", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_seeded_templates()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/looptemplates", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // TemplateSeeder runs on startup; the seeded list may be empty or non-empty.
        var items = await response.Content.ReadFromJsonAsync<object[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(items);
    }

    private static JsonElement PropertyIgnoringCase(JsonElement obj, string name)
        => obj.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public async Task Node_outputs_lists_the_fixed_and_reserved_outputs_of_each_node_type()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/looptemplates/node-outputs", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var map = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;

        static List<(string Name, bool Reserved)> OutputsOf(JsonElement map, string type)
            => PropertyIgnoringCase(map, type).EnumerateArray()
                .Select(o => (
                    PropertyIgnoringCase(o, "name").GetString()!,
                    o.EnumerateObject().Any(p => string.Equals(p.Name, "reserved", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)))
                .ToList();

        foreach (var type in new[] { "Start", "Cmd", "AI", "Human", "Prompt" })
            Assert.Equal(new[] { ("OnFailure", false), ("OnSuccess", false) }, OutputsOf(map, type).OrderBy(o => o.Name, StringComparer.Ordinal));
        Assert.Equal(new[] { ("OnFailure", false) }, OutputsOf(map, "Condition"));
        Assert.Empty(OutputsOf(map, "Cleanup"));

        // The PR node's reserved outputs are the ones the heartbeat fires: one definition.
        var pr = OutputsOf(map, "PR");
        Assert.Equal(
            PrNodeEdges.ByPriority.Select(n => (n, true)).Append(("OnSuccess", false)).Append(("OnFailure", false)).OrderBy(o => o.Item1, StringComparer.Ordinal),
            pr.OrderBy(o => o.Name, StringComparer.Ordinal));
    }

    private const string LegacyDocument =
        "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"Old\",\"description\":\"\",\"recoveryPolicy\":\"AutoResume\"," +
        "\"nodes\":[" +
        "{\"id\":\"start\",\"type\":\"Start\",\"label\":\"Start\",\"config\":{}}," +
        "{\"id\":\"review\",\"type\":\"Human\",\"label\":\"Review\",\"config\":{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\"]}}," +
        "{\"id\":\"cleanup\",\"type\":\"Cleanup\",\"label\":\"Cleanup\",\"config\":{}}]," +
        "\"edges\":[" +
        "{\"id\":\"e1\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"review\",\"edgeType\":\"OnSuccess\",\"name\":null}," +
        "{\"id\":\"e2\",\"sourceNodeId\":\"review\",\"targetNodeId\":\"cleanup\",\"edgeType\":\"Custom\",\"name\":\"Respond\"}]}";

    [Fact]
    public async Task Upgrade_document_turns_a_v1_export_into_v2()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/looptemplates/upgrade-document",
            new { document = LegacyDocument }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        var returned = PropertyIgnoringCase(body, "document");
        var doc = returned.ValueKind == JsonValueKind.String ? JsonDocument.Parse(returned.GetString()!).RootElement : returned;
        Assert.Equal("ild-loop-template/v2", doc.GetProperty("$schema").GetString());
        var review = doc.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "review").GetProperty("config");
        Assert.False(review.TryGetProperty("customEdges", out _));
        Assert.Contains(review.GetProperty("outputs").EnumerateArray(), o => o.GetProperty("name").GetString() == "Respond");
    }

    [Fact]
    public async Task Upgrade_document_refuses_a_document_that_is_not_a_json_object()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/looptemplates/upgrade-document",
            new { document = "[1,2]" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
