using ILD.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Core.Services.Remote;

namespace ILD.Tests.Integration;

public class LoopTemplatesIntegrationTests(SharedApiFactory factory) : IClassFixture<SharedApiFactory>
{
    private static JsonElement PropertyIgnoringCase(JsonElement obj, string name)
        => obj.EnumerateObject().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public async Task Node_outputs_lists_the_fixed_and_reserved_outputs_of_each_node_type()
    {
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

        foreach (var type in new[] { "Start", "Cmd", "AI", "Human", "Prompt", "SetVariable" })
            Assert.Equal(new[] { ("OnFailure", false), ("OnSuccess", false) }, OutputsOf(map, type).OrderBy(o => o.Name, StringComparer.Ordinal));
        Assert.Equal(new[] { ("OnFailure", false) }, OutputsOf(map, "Condition"));
        Assert.Empty(OutputsOf(map, "Cleanup"));

        // The PR node's reserved outputs are the ones the heartbeat fires: one definition.
        var pr = OutputsOf(map, "PR");
        Assert.Equal(
            LoopOutputs.ReservedPr.Select(n => (n, true)).Append(("OnSuccess", false)).Append(("OnFailure", false)).OrderBy(o => o.Item1, StringComparer.Ordinal),
            pr.OrderBy(o => o.Name, StringComparer.Ordinal));
    }

    private static object SetVariableLoop(object assignments) => new
    {
        name = $"Keep the answer {Guid.NewGuid():N}",
        description = "",
        nodes = new object[]
        {
            new { id = "start", type = "Start", label = "Start", config = new { } },
            new { id = "keep", type = "SetVariable", label = "Keep", config = new { assignments } },
            new { id = "cleanup", type = "Cleanup", label = "Cleanup", config = new { } },
        },
        edges = new object[]
        {
            new { id = "e1", sourceNodeId = "start", targetNodeId = "keep", edgeType = "OnSuccess" },
            new { id = "e2", sourceNodeId = "keep", targetNodeId = "cleanup", edgeType = "OnSuccess" },
        },
    };

    [Fact]
    public async Task A_set_variable_node_saves_and_loads_back_with_its_assignments_and_fixed_outputs()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var assignments = new object[]
        {
            new { name = "handoff", value = "{{Node.Input}}" },
            new { name = "phase", value = "review" },
        };

        var created = await client.PostAsJsonAsync("/api/v1/looptemplates", SetVariableLoop(assignments), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("id").GetString();
        var loaded = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/looptemplates/{id}", TestContext.Current.CancellationToken)).RootElement;

        var keep = loaded.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("label").GetString() == "Keep");
        Assert.Equal("SetVariable", keep.GetProperty("type").GetString());
        var config = keep.GetProperty("config");
        Assert.Equal(
            new (string?, string?)[] { ("handoff", "{{Node.Input}}"), ("phase", "review") },
            config.GetProperty("assignments").EnumerateArray().Select(a => (a.GetProperty("name").GetString(), a.GetProperty("value").GetString())));
        Assert.Equal(
            new[] { "OnSuccess", "OnFailure" },
            config.GetProperty("outputs").EnumerateArray().Select(o => o.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Saving_a_set_variable_node_with_an_invalid_name_is_refused_with_the_reason()
    {
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/looptemplates",
            SetVariableLoop(new object[] { new { name = "bad-name", value = "x" } }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("error").GetString();
        Assert.Contains("keep", error);
        Assert.Contains("assignment 1", error);
        Assert.Contains("bad-name", error);
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
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/v1/looptemplates/upgrade-document",
            new { document = "[1,2]" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
