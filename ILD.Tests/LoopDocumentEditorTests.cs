using System.Text.Json;
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
    // A valid ild-loop-template/v1 with Start → AI → Cleanup. The AI prompt carries
    // a newline and embedded quotes so escaping round-trips are exercised. Built via
    // the serializer so the on-disk escaping is exactly what a real document has.
    private const string AiPrompt = "Review the code.\nBe \"strict\" about tests.";

    private static string ValidDocument(string? aiPrompt = null) =>
        JsonSerializer.Serialize(new
        {
            schema = "ild-loop-template/v1",
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
        const string noStart = "{\"$schema\":\"ild-loop-template/v1\",\"name\":\"x\",\"nodes\":[],\"edges\":[]}";
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
}
