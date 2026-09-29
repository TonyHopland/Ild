using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// Unit tests for <see cref="LoopDocumentEditor"/> — the engine behind the scoped
/// loop-edit tools (loop editor context, ADR-0011). The contract under test:
/// unique-match on string replaces, decode→edit→re-encode of node fields without
/// the caller ever touching JSON escaping, and reject-on-invalid leaving the
/// document unchanged.
/// </summary>
public class LoopDocumentEditorTests
{
    // A valid ild-loop-template/v2 with Start → AI → Cleanup. The AI prompt carries
    // a newline and embedded quotes so escaping round-trips are exercised. Built via
    // the serializer so the on-disk escaping is exactly what a real document has.
    private const string AiPrompt = "Review the code.\nBe \"strict\" about tests.";

    private static string ValidDocument(string? aiPrompt = null, string schema = "ild-loop-template/v2") =>
        JsonSerializer.Serialize(new
        {
            schema,
            name = "Test Loop",
            description = "",
            recoveryPolicy = "AutoResume",
            nodes = new object[]
            {
                new { id = "start", type = "Start", label = "Start", config = new { } },
                new { id = "ai", type = "AI", label = "Reviewer", config = new { prompt = aiPrompt ?? AiPrompt, aiProviderId = "prov" } },
                new { id = "cleanup", type = "Cleanup", label = "Cleanup", config = new { } },
            },
            edges = new object[]
            {
                new { id = "e1", sourceNodeId = "start", targetNodeId = "ai", edgeType = "OnSuccess", name = (string?)null },
                new { id = "e2", sourceNodeId = "ai", targetNodeId = "cleanup", edgeType = "OnSuccess", name = (string?)null },
            },
        }).Replace("\"schema\":", "\"$schema\":");

    private static string PromptOf(string document, string nodeId)
    {
        using var doc = JsonDocument.Parse(document);
        foreach (var node in doc.RootElement.GetProperty("nodes").EnumerateArray())
        {
            if (node.GetProperty("id").GetString() == nodeId)
                return node.GetProperty("config").GetProperty("prompt").GetString()!;
        }
        throw new InvalidOperationException($"node {nodeId} not found");
    }

    // -- get_loop_node ---------------------------------------------------------

    [Fact]
    public void GetNode_returns_the_node_with_decoded_config()
    {
        var (found, nodeJson, error) = LoopDocumentEditor.GetNode(ValidDocument(), "ai");

        Assert.True(found);
        Assert.Null(error);
        using var node = JsonDocument.Parse(nodeJson!);
        Assert.Equal("AI", node.RootElement.GetProperty("type").GetString());
        // The prompt comes back as a single decoded JSON string (real newline/quotes),
        // not a double-encoded blob.
        Assert.Equal(AiPrompt, node.RootElement.GetProperty("config").GetProperty("prompt").GetString());
    }

    [Fact]
    public void GetNode_reports_an_unknown_node()
    {
        var (found, nodeJson, error) = LoopDocumentEditor.GetNode(ValidDocument(), "does-not-exist");

        Assert.False(found);
        Assert.Null(nodeJson);
        Assert.Contains("does-not-exist", error);
    }

    // -- edit_loop_node_field: unique-match --------------------------------------

    [Fact]
    public void EditNodeField_replaces_a_unique_match_and_reencodes()
    {
        // The replacement itself contains a quote and a newline; the caller passes
        // plain text and the server must produce correctly-escaped JSON.
        var result = LoopDocumentEditor.EditNodeField(
            ValidDocument(), "ai", "prompt", "strict", "very \"strict\"\nindeed");

        Assert.True(result.Applied);
        Assert.Equal(1, result.MatchCount);
        Assert.Empty(result.ValidationErrors);
        Assert.NotNull(result.Document);
        Assert.Equal("Review the code.\nBe \"very \"strict\"\nindeed\" about tests.", PromptOf(result.Document!, "ai"));
    }

    [Fact]
    public void EditNodeField_reports_zero_matches_and_changes_nothing()
    {
        var result = LoopDocumentEditor.EditNodeField(
            ValidDocument(), "ai", "prompt", "not present in the prompt", "x");

        Assert.False(result.Applied);
        Assert.Equal(0, result.MatchCount);
        Assert.Null(result.Document);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public void EditNodeField_reports_multiple_matches_with_the_count()
    {
        // "code" appears once in the seed prompt; craft a prompt with two.
        var doc = ValidDocument("code code");
        var result = LoopDocumentEditor.EditNodeField(doc, "ai", "prompt", "code", "x");

        Assert.False(result.Applied);
        Assert.Equal(2, result.MatchCount);
        Assert.Null(result.Document);
        Assert.Contains("2", result.Error);
    }

    [Fact]
    public void EditNodeField_rejects_an_unknown_node()
    {
        var result = LoopDocumentEditor.EditNodeField(ValidDocument(), "ghost", "prompt", "a", "b");

        Assert.False(result.Applied);
        Assert.Contains("ghost", result.Error);
    }

    [Fact]
    public void EditNodeField_rejects_a_missing_field()
    {
        var result = LoopDocumentEditor.EditNodeField(ValidDocument(), "ai", "nope", "a", "b");

        Assert.False(result.Applied);
        Assert.Contains("nope", result.Error);
    }

    // -- reject-on-invalid -------------------------------------------------------

    [Fact]
    public void EditNodeField_rejecting_an_invalid_result_leaves_the_document_unchanged()
    {
        // Introduce an unknown placeholder: the graph validator rejects it.
        var result = LoopDocumentEditor.EditNodeField(
            ValidDocument(), "ai", "prompt", "Review the code.", "Review {{Bogus.Placeholder}}");

        Assert.False(result.Applied);
        Assert.Null(result.Document);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.Contains(result.ValidationErrors, e => e.Contains("Bogus.Placeholder"));
    }

    // -- set_loop_node_field -----------------------------------------------------

    [Fact]
    public void SetNodeField_overwrites_the_whole_field()
    {
        var result = LoopDocumentEditor.SetNodeField(ValidDocument(), "ai", "prompt", "A brand new prompt.");

        Assert.True(result.Applied);
        Assert.Equal("A brand new prompt.", PromptOf(result.Document!, "ai"));
    }

    [Fact]
    public void SetNodeField_creates_a_missing_field()
    {
        var result = LoopDocumentEditor.SetNodeField(ValidDocument(), "start", "command", "echo hi");

        Assert.True(result.Applied);
        using var doc = JsonDocument.Parse(result.Document!);
        var start = doc.RootElement.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("id").GetString() == "start");
        Assert.Equal("echo hi", start.GetProperty("config").GetProperty("command").GetString());
    }

    // -- edit_loop_file ----------------------------------------------------------

    [Fact]
    public void EditFile_replaces_a_unique_raw_match()
    {
        // The document is compact JSON (no spaces after colons), so match its bytes.
        var result = LoopDocumentEditor.EditFile(ValidDocument(), "\"label\":\"Reviewer\"", "\"label\":\"Strict Reviewer\"");

        Assert.True(result.Applied);
        Assert.Equal(1, result.MatchCount);
        using var doc = JsonDocument.Parse(result.Document!);
        var ai = doc.RootElement.GetProperty("nodes").EnumerateArray().First(n => n.GetProperty("id").GetString() == "ai");
        Assert.Equal("Strict Reviewer", ai.GetProperty("label").GetString());
    }

    [Fact]
    public void EditFile_reports_multiple_matches()
    {
        var result = LoopDocumentEditor.EditFile(ValidDocument(), "\"OnSuccess\"", "\"OnSuccess\"");

        Assert.False(result.Applied);
        Assert.Equal(2, result.MatchCount);
        Assert.Null(result.Document);
    }

    [Fact]
    public void EditFile_rejects_a_result_that_is_not_valid_json()
    {
        var result = LoopDocumentEditor.EditFile(ValidDocument(), "\"nodes\":", "\"nodes\"");

        Assert.False(result.Applied);
        Assert.Null(result.Document);
        Assert.Contains("invalid JSON", result.Error);
    }

    [Fact]
    public void EditFile_rejects_a_result_that_breaks_the_graph()
    {
        // Redirect the Start→AI edge at a node that does not exist: AI/Cleanup become
        // unreachable, so graph validation fails.
        var result = LoopDocumentEditor.EditFile(ValidDocument(), "\"targetNodeId\":\"ai\"", "\"targetNodeId\":\"nowhere\"");

        Assert.False(result.Applied);
        Assert.Null(result.Document);
        Assert.NotEmpty(result.ValidationErrors);
    }

    // -- update_current_loop retrofit (ReplaceDocument) --------------------------

    [Fact]
    public void ReplaceDocument_accepts_a_valid_document()
    {
        var result = LoopDocumentEditor.ReplaceDocument(ValidDocument());

        Assert.True(result.Applied);
        Assert.Empty(result.ValidationErrors);
        Assert.NotNull(result.Document);
    }

    [Fact]
    public void ReplaceDocument_rejects_a_graph_without_a_start_node()
    {
        const string noStart = "{\"$schema\":\"ild-loop-template/v2\",\"name\":\"x\",\"nodes\":[],\"edges\":[]}";
        var result = LoopDocumentEditor.ReplaceDocument(noStart);

        Assert.False(result.Applied);
        Assert.Null(result.Document);
        Assert.NotEmpty(result.ValidationErrors);
    }

    [Fact]
    public void ReplaceDocument_reports_malformed_json_as_an_error_not_validation()
    {
        var result = LoopDocumentEditor.ReplaceDocument("{ not json");

        Assert.False(result.Applied);
        Assert.Null(result.Document);
        Assert.Empty(result.ValidationErrors);
        Assert.Contains("not valid JSON", result.Error);
    }

    // -- ild-loop-template/v2: declared outputs ----------------------------------

    private static JsonObject Root(string document) => (JsonObject)JsonNode.Parse(document)!;

    private static JsonObject ConfigOf(string document, string nodeId) =>
        (JsonObject)Root(document)["nodes"]!.AsArray().Single(n => (string)n!["id"]! == nodeId)!["config"]!;

    // A v1 document an AI may still send from memory: the Human node defines its
    // "Respond" output through customEdges.
    private const string LegacyHumanDocument =
        "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"Old\",\"description\":\"\",\"recoveryPolicy\":\"AutoResume\"," +
        "\"nodes\":[" +
        "{\"id\":\"start\",\"type\":\"Start\",\"label\":\"Start\",\"config\":{}}," +
        "{\"id\":\"review\",\"type\":\"Human\",\"label\":\"Review\",\"config\":{\"prompt\":\"ok?\",\"customEdges\":[\"Respond\"]}}," +
        "{\"id\":\"cleanup\",\"type\":\"Cleanup\",\"label\":\"Cleanup\",\"config\":{}}]," +
        "\"edges\":[" +
        "{\"id\":\"e1\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"review\",\"edgeType\":\"OnSuccess\",\"name\":null}," +
        "{\"id\":\"e2\",\"sourceNodeId\":\"review\",\"targetNodeId\":\"cleanup\",\"edgeType\":\"Custom\",\"name\":\"Respond\"}]}";

    [Fact]
    public void ReplaceDocument_upgrades_a_v1_document_instead_of_rejecting_it()
    {
        var result = LoopDocumentEditor.ReplaceDocument(LegacyHumanDocument);

        Assert.True(result.Applied, string.Join("; ", result.ValidationErrors) + result.Error);
        Assert.Equal("ild-loop-template/v2", (string)Root(result.Document!)["$schema"]!);
        var review = ConfigOf(result.Document!, "review");
        Assert.False(review.ContainsKey("customEdges"));
        Assert.Contains(review["outputs"]!.AsArray(), o => (string)o!["name"]! == "Respond");
    }

    [Fact]
    public void ReplaceDocument_keeps_a_v2_document_byte_for_byte()
    {
        var document = ValidDocument();

        var result = LoopDocumentEditor.ReplaceDocument(document);

        Assert.Equal(document, result.Document);
    }

    [Fact]
    public void EditFile_returns_an_edited_v1_document_upgraded_to_v2()
    {
        var result = LoopDocumentEditor.EditFile(LegacyHumanDocument, "\"label\":\"Review\"", "\"label\":\"Sign off\"");

        Assert.True(result.Applied, string.Join("; ", result.ValidationErrors) + result.Error);
        Assert.Equal("ild-loop-template/v2", (string)Root(result.Document!)["$schema"]!);
        Assert.Contains(ConfigOf(result.Document!, "review")["outputs"]!.AsArray(), o => (string)o!["name"]! == "Respond");
    }

    [Fact]
    public void EditFile_on_a_v2_document_changes_only_the_edited_bytes()
    {
        var document = ValidDocument();

        var result = LoopDocumentEditor.EditFile(document, "\"label\":\"Reviewer\"", "\"label\":\"Strict Reviewer\"");

        Assert.Equal(document.Replace("\"label\":\"Reviewer\"", "\"label\":\"Strict Reviewer\""), result.Document);
    }

    // Start → AI (declares and wires "reject") → Condition switch (declares its case and default) → Cleanup.
    private const string StructuredDocument =
        "{\"$schema\":\"ild-loop-template/v2\",\"name\":\"Live\",\"description\":\"\",\"recoveryPolicy\":\"AutoResume\"," +
        "\"nodes\":[" +
        "{\"id\":\"start\",\"type\":\"Start\",\"label\":\"Start\",\"config\":{}}," +
        "{\"id\":\"ai\",\"type\":\"AI\",\"label\":\"Reviewer\",\"config\":{\"prompt\":\"Review it\",\"outputs\":[{\"name\":\"reject\"}]}}," +
        "{\"id\":\"gate\",\"type\":\"Condition\",\"label\":\"Gate\",\"config\":{\"cases\":[{\"variant\":\"PrExists\",\"edgeName\":\"has-pr\"}],\"defaultEdge\":\"otherwise\",\"outputs\":[{\"name\":\"has-pr\"},{\"name\":\"otherwise\"}]}}," +
        "{\"id\":\"cleanup\",\"type\":\"Cleanup\",\"label\":\"Cleanup\",\"config\":{}}]," +
        "\"edges\":[" +
        "{\"id\":\"e1\",\"sourceNodeId\":\"start\",\"targetNodeId\":\"ai\",\"edgeType\":\"OnSuccess\",\"name\":null}," +
        "{\"id\":\"e2\",\"sourceNodeId\":\"ai\",\"targetNodeId\":\"gate\",\"edgeType\":\"OnSuccess\",\"name\":null}," +
        "{\"id\":\"e3\",\"sourceNodeId\":\"gate\",\"targetNodeId\":\"cleanup\",\"edgeType\":\"Custom\",\"name\":\"has-pr\"}," +
        "{\"id\":\"e4\",\"sourceNodeId\":\"gate\",\"targetNodeId\":\"cleanup\",\"edgeType\":\"Custom\",\"name\":\"otherwise\"}," +
        "{\"id\":\"e5\",\"sourceNodeId\":\"ai\",\"targetNodeId\":\"cleanup\",\"edgeType\":\"Custom\",\"name\":\"reject\"}]}";

    [Theory]
    [InlineData("ai", "outputs", "[{\"name\":\"reject\",\"visible\":false,\"color\":\"x\"}]")]
    [InlineData("ai", "matchRules", "[{\"pattern\":\"REJECT\",\"edgeName\":\"reject\"}]")]
    [InlineData("ai", "toolAllowlist", "[\"read\",\"write\"]")]
    [InlineData("gate", "cases", "[{\"variant\":\"HasTag\",\"tag\":\"urgent\",\"edgeName\":\"has-pr\"}]")]
    public void SetNodeField_stores_a_structured_field_as_json(string nodeId, string field, string value)
    {
        var result = LoopDocumentEditor.SetNodeField(StructuredDocument, nodeId, field, value);

        Assert.True(result.Applied, string.Join("; ", result.ValidationErrors) + result.Error);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(value), ConfigOf(result.Document!, nodeId)[field]),
            ConfigOf(result.Document!, nodeId)[field]?.ToJsonString());
    }

    [Theory]
    [InlineData("outputs", "approve")]
    [InlineData("outputs", "{\"name\":\"approve\"}")]
    [InlineData("matchRules", "[{\"pattern\":\"x\"")]
    [InlineData("toolAllowlist", "\"read\"")]
    public void SetNodeField_rejects_a_structured_value_that_is_not_a_json_array(string field, string value)
    {
        var result = LoopDocumentEditor.SetNodeField(StructuredDocument, "ai", field, value);

        Assert.False(result.Applied);
        Assert.Null(result.Document);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void SetNodeField_keeps_a_text_field_as_text_even_when_it_looks_like_json()
    {
        var result = LoopDocumentEditor.SetNodeField(StructuredDocument, "ai", "prompt", "[{\"name\":\"x\"}]");

        Assert.True(result.Applied);
        Assert.Equal("[{\"name\":\"x\"}]", (string)ConfigOf(result.Document!, "ai")["prompt"]!);
    }

    [Fact]
    public void An_ai_can_add_rename_and_change_an_output_with_the_targeted_tools()
    {
        const string rule = "[{\"pattern\":\"REJECT\",\"edgeName\":\"reject\"}]";
        var document = ValidDocument();

        // A rule that refers to an output nobody declared is refused, and says what to do.
        var early = LoopDocumentEditor.SetNodeField(document, "ai", "matchRules", rule);
        Assert.False(early.Applied);
        Assert.Contains(early.ValidationErrors, e => e.Contains("'reject'") && e.Contains("outputs"));

        string Apply(LoopEditResult step)
        {
            Assert.True(step.Applied, string.Join("; ", step.ValidationErrors) + step.Error);
            return step.Document!;
        }

        string AddEdge(string doc, string id, string name)
        {
            var edgesAnchor = System.Text.RegularExpressions.Regex.Match(doc, "\"edges\"\\s*:\\s*\\[").Value;
            return Apply(LoopDocumentEditor.EditFile(doc, edgesAnchor,
                edgesAnchor + $"{{\"id\":\"{id}\",\"sourceNodeId\":\"ai\",\"targetNodeId\":\"cleanup\",\"edgeType\":\"Custom\",\"name\":\"{name}\"}},"));
        }

        // A rule routing to a declared output that has no edge is refused too.
        var declared = Apply(LoopDocumentEditor.SetNodeField(document, "ai", "outputs", "[{\"name\":\"reject\"}]"));
        var unwired = LoopDocumentEditor.SetNodeField(declared, "ai", "matchRules", rule);
        Assert.False(unwired.Applied);
        Assert.Contains(unwired.ValidationErrors, e => e.Contains("'reject'") && e.Contains("no edge"));

        // Add: declare the output, wire the edge, add the rule.
        document = declared;
        document = AddEdge(document, "e-reject", "reject");
        document = Apply(LoopDocumentEditor.SetNodeField(document, "ai", "matchRules", rule));

        // Rename: add the new name and wire it, repoint the rule, then remove the
        // old edge and the old name.
        document = Apply(LoopDocumentEditor.SetNodeField(document, "ai", "outputs", "[{\"name\":\"reject\"},{\"name\":\"rework\"}]"));
        document = AddEdge(document, "e-rework", "rework");
        document = Apply(LoopDocumentEditor.SetNodeField(document, "ai", "matchRules", rule.Replace("reject", "rework")));
        var idAt = document.IndexOf("\"e-reject\"", StringComparison.Ordinal);
        var objectStart = document.LastIndexOf('{', idAt);
        var afterObject = document.IndexOf('}', idAt) + 1;
        var oldEdge = document[objectStart..(document.IndexOf(',', afterObject) + 1)];
        document = Apply(LoopDocumentEditor.EditFile(document, oldEdge, ""));
        document = Apply(LoopDocumentEditor.SetNodeField(document, "ai", "outputs", "[{\"name\":\"rework\"}]"));

        // Change: give the output a setting.
        document = Apply(LoopDocumentEditor.SetNodeField(document, "ai", "outputs", "[{\"name\":\"rework\",\"visible\":false}]"));

        var ai = ConfigOf(document, "ai");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[{\"name\":\"rework\",\"visible\":false}]"), ai["outputs"]));
        Assert.Equal("rework", (string)ai["matchRules"]![0]!["edgeName"]!);
        var edges = Root(document)["edges"]!.AsArray();
        Assert.DoesNotContain(edges, e => (string?)e!["name"] == "reject");
        Assert.Equal("ai", (string)edges.Single(e => (string?)e!["name"] == "rework")!["sourceNodeId"]!);
    }
}
