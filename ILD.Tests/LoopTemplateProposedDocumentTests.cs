using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations;
using ILD.Data.Stores.Interfaces;
using Moq;

namespace ILD.Tests;

/// <summary>
/// What a proposed loop document is told when it fails a save's checks: the
/// document-level refusals word for word, the graph rules as validation errors,
/// and a valid one back as the request and document a save of it would use.
/// </summary>
public class LoopTemplateProposedDocumentTests
{
    private static readonly LoopTemplateManager Manager = new(Mock.Of<ILoopTemplateStore>());

    private const string Graph =
        """
        "nodes":[
          {"id":"s","type":"Start","label":"Start","config":{}},
          {"id":"a","type":"Cmd","label":"build","config":{"command":"echo hi"}},
          {"id":"c","type":"Cleanup","label":"Cleanup","config":{}}],
        "edges":[
          {"id":"e1","sourceNodeId":"s","targetNodeId":"a","edgeType":"OnSuccess"},
          {"id":"e2","sourceNodeId":"a","targetNodeId":"c","edgeType":"OnSuccess"}]
        """;

    public static TheoryData<string, string, bool> Refusals => new()
    {
        {
            "{ not json",
            "The document is not valid JSON: ",
            true
        },
        {
            "[1,2]",
            "The document must be a JSON object: the whole ild-loop-template/v2 document get_loop_template returns.",
            false
        },
        {
            "null",
            "The document must be a JSON object: the whole ild-loop-template/v2 document get_loop_template returns.",
            false
        },
        {
            """{"name":"x","nodes":[],"edges":[]}""",
            "The document has no $schema. Send the whole ild-loop-template/v2 document get_loop_template returns, with your changes.",
            false
        },
        {
            """{"$schema":"ild-loop-template/v9","name":"x","recoveryPolicy":"AutoResume","nodes":[],"edges":[]}""",
            "$schema must be \"ild-loop-template/v2\" (a ild-loop-template/v1 document is upgraded to it).",
            false
        },
        {
            """{"$schema":2,"name":"x","recoveryPolicy":"AutoResume","nodes":[],"edges":[]}""",
            "$schema must be \"ild-loop-template/v2\" (a ild-loop-template/v1 document is upgraded to it).",
            false
        },
        {
            """{"$schema":"ild-loop-template/v2","name":"x","recoveryPolicy":"Sometimes","nodes":[],"edges":[]}""",
            "recoveryPolicy must be one of AutoResume, NeedsReview, Cancel.",
            false
        },
        {
            """{"$schema":"ild-loop-template/v2","name":"x","recoveryPolicy":"99","nodes":[],"edges":[]}""",
            "recoveryPolicy must be one of AutoResume, NeedsReview, Cancel.",
            false
        },
        {
            """{"$schema":"ild-loop-template/v2","name":"x","nodes":[],"edges":[]}""",
            "recoveryPolicy must be one of AutoResume, NeedsReview, Cancel.",
            false
        },
        {
            """{"$schema":"ild-loop-template/v2","name":"x","recoveryPolicy":"AutoResume","nodes":"all of them","edges":[]}""",
            "The document's nodes or edges are not in the ild-loop-template/v2 shape: ",
            true
        },
        {
            """{"$schema":"ild-loop-template/v2","name":"x","recoveryPolicy":"AutoResume","nodes":[],"edges":[{"sourceNodeId":5}]}""",
            "The document's nodes or edges are not in the ild-loop-template/v2 shape: ",
            true
        },
        {
            $$"""{"$schema":"ild-loop-template/v2","name":"","recoveryPolicy":"AutoResume",{{Graph}}}""",
            "The Name field is required.",
            false
        },
        {
            $$"""{"$schema":"ild-loop-template/v2","name":7,"recoveryPolicy":"AutoResume",{{Graph}}}""",
            "The Name field is required.",
            false
        },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_document_a_save_cannot_read_is_refused_with_what_is_wrong(string document, string error, bool onlyTheStartIsPinned)
    {
        var result = Manager.CheckProposedDocument(document);

        if (onlyTheStartIsPinned) Assert.StartsWith(error, result.Error);
        else Assert.Equal(error, result.Error);
        Assert.Null(result.Request);
        Assert.Null(result.Document);
        Assert.Empty(result.ValidationErrors);
    }

public static TheoryData<string, string> OversizedOrAmbiguous => new()
    {
        { "name over 256", $$"""{"$schema":"ild-loop-template/v2","name":"{{new string('n', 257)}}","recoveryPolicy":"AutoResume",{{Graph}}}""" },
        { "description over 8192", $$"""{"$schema":"ild-loop-template/v2","name":"x","description":"{{new string('d', 8193)}}","recoveryPolicy":"AutoResume",{{Graph}}}""" },
        { "a key given twice", $$"""{"name":"Twice","$schema":"ild-loop-template/v2","name":"x","recoveryPolicy":"AutoResume",{{Graph}}}""" },
    };

    [Theory]
    [MemberData(nameof(OversizedOrAmbiguous))]
    public void A_document_with_an_oversized_field_or_a_repeated_key_is_refused(string _, string document)
    {
        var result = Manager.CheckProposedDocument(document);

        Assert.False(string.IsNullOrWhiteSpace(result.Error), "the refusal gives no reason");
        Assert.Null(result.Request);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_graph_that_breaks_the_save_rules_comes_back_as_validation_errors()
    {
        var result = Manager.CheckProposedDocument(
            """{"$schema":"ild-loop-template/v2","name":"x","recoveryPolicy":"AutoResume","nodes":[],"edges":[]}""");

        Assert.Null(result.Error);
        Assert.Null(result.Request);
        Assert.Null(result.Document);
        Assert.NotEmpty(result.ValidationErrors);
    }

    [Theory]
    [InlineData("ild-loop-template/v2", "needsreview")]
    [InlineData("ild-loop-template/v1", "NeedsReview")]
    public void A_valid_document_comes_back_as_the_request_and_v2_document_a_save_would_use(string schema, string policy)
    {
        var result = Manager.CheckProposedDocument(
            $$"""{"$schema":"{{schema}}","name":"Dev Loop","description":"Builds it","recoveryPolicy":"{{policy}}",{{Graph}}}""");

        Assert.Null(result.Error);
        Assert.Empty(result.ValidationErrors);
        var request = Assert.IsType<ILD.Data.DTOs.LoopTemplateCreateRequest>(result.Request);
        Assert.Equal("Dev Loop", request.Name);
        Assert.Equal("Builds it", request.Description);
        Assert.Equal(ILD.Data.Enums.RecoveryPolicy.NeedsReview, request.RecoveryPolicy);
        Assert.Equal(["s", "a", "c"], request.Nodes.Select(n => n.Id));
        Assert.Equal(["e1", "e2"], request.Edges.Select(e => e.Id));
        var stored = JsonNode.Parse(result.Document!)!.AsObject();
        Assert.Equal("ild-loop-template/v2", (string?)stored["$schema"]);
        Assert.Equal("Dev Loop", (string?)stored["name"]);
        Assert.Equal("NeedsReview", (string?)stored["recoveryPolicy"]);
        Assert.Equal(3, stored["nodes"]!.AsArray().Count);
    }
}
