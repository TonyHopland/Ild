using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// The result of a scoped (or full-document) edit against a live
/// <c>ild-loop-template/v1</c> document. This is the synchronous ack every loop
/// edit surface returns (loop editor context, ADR-0011): the agent learns
/// <em>now</em> whether its edit landed instead of re-reading the loop next turn.
///
/// <para>
/// Exactly one of three terminal states holds:
/// <list type="bullet">
///   <item><b>Applied</b> — the mutation matched and the resulting document
///         passed graph validation. <see cref="Document"/> is the new document;
///         the caller pushes it to the canvas and stashes it.</item>
///   <item><b>Match error</b> — a string-replace surface found zero or multiple
///         occurrences, or a structural problem (unknown node/field). <see cref="Error"/>
///         explains it, <see cref="Document"/> is null, nothing changes.</item>
///   <item><b>Invalid</b> — the mutation matched but the resulting graph failed
///         <see cref="LoopTemplateValidator"/>. <see cref="ValidationErrors"/> is
///         non-empty, <see cref="Document"/> is null, the canvas is left untouched.</item>
/// </list>
/// </para>
/// </summary>
public sealed record LoopEditResult(
    bool Applied,
    int MatchCount,
    IReadOnlyList<string> ValidationErrors,
    string? Document,
    string? Error,
    string? Summary)
{
    public static LoopEditResult MatchFailure(int matchCount, string error) =>
        new(false, matchCount, Array.Empty<string>(), null, error, null);

    public static LoopEditResult Invalid(int matchCount, IReadOnlyList<string> validationErrors) =>
        new(false, matchCount, validationErrors, null, null, null);

    public static LoopEditResult Success(int matchCount, string document, string summary) =>
        new(true, matchCount, Array.Empty<string>(), document, null, summary);
}

/// <summary>
/// Targeted, in-place edits over a live <c>ild-loop-template/v1</c> document
/// (loop editor context, ADR-0011). Purpose: let an agent change one node's prompt
/// or one substring of the raw JSON <em>without</em> re-serializing the whole
/// document, which used to corrupt unrelated nodes and gave no validation ack.
///
/// <para>
/// Every mutating operation is stateless and pure: it takes the current document
/// text and returns a <see cref="LoopEditResult"/>. String-replace surfaces
/// (<see cref="EditNodeField"/>, <see cref="EditFile"/>) require a <b>unique</b>
/// match — zero or multiple matches change nothing and report why — so an edit can
/// never silently hit the wrong occurrence. Field edits operate on the
/// <em>decoded</em> string value: the agent never handles JSON string escaping, the
/// re-encode is done here. Every successful mutation is re-validated through
/// <see cref="LoopTemplateValidator"/> before it is returned, so an edit that would
/// break the graph is rejected up front and the canvas stays as it was.
/// </para>
///
/// This class is the single engine behind all of <c>LoopTools</c> (MCP),
/// <c>ToolDescriptors</c> (Pi) and <c>AgentController</c> — the three surfaces
/// ADR-0009 requires to stay in lockstep.
/// </summary>
public static class LoopDocumentEditor
{
    private static readonly JsonSerializerOptions GraphParseOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Return one node's <c>{ id, type, label, config }</c> as indented JSON. The
    /// config's prompt/text fields are ordinary nested JSON strings, so they come
    /// back decoded (a single JSON layer) — the agent reads real text and never has
    /// to unescape a stringified blob. Returns a null document with an error when
    /// the document is unparseable or the node is unknown.
    /// </summary>
    public static (bool Found, string? NodeJson, string? Error) GetNode(string document, string nodeId)
    {
        if (!TryParseRoot(document, out var root, out var parseError))
            return (false, null, parseError);

        var node = FindNode(root, nodeId);
        if (node == null)
            return (false, null, $"No node with id '{nodeId}' exists in the current loop.");

        return (true, node.ToJsonString(OutputOptions), null);
    }

    /// <summary>
    /// Replace a unique occurrence of <paramref name="oldString"/> inside a node
    /// config field's <em>decoded</em> text with <paramref name="newString"/>, then
    /// re-encode the field back into the document. This is the primary fix for
    /// prompt-only edits: the model works on plain text and the server owns the JSON
    /// escaping. Zero or multiple matches change nothing.
    /// </summary>
    public static LoopEditResult EditNodeField(
        string document, string nodeId, string field, string oldString, string newString)
    {
        if (string.IsNullOrEmpty(oldString))
            return LoopEditResult.MatchFailure(0, "old_string must not be empty.");

        if (!TryResolveField(document, nodeId, field, out var root, out var config, out var current, out var error))
            return LoopEditResult.MatchFailure(0, error!);

        if (current is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return LoopEditResult.MatchFailure(0,
                $"Field '{field}' on node '{nodeId}' is not a text field; use set_loop_node_field to overwrite it or edit_loop_file for structural changes.");

        var text = value.GetValue<string>();
        var count = CountOccurrences(text, oldString);
        if (count == 0)
            return LoopEditResult.MatchFailure(0,
                $"old_string was not found in field '{field}' of node '{nodeId}'; no change made.");
        if (count > 1)
            return LoopEditResult.MatchFailure(count,
                $"old_string matches {count} times in field '{field}' of node '{nodeId}'; make it unique (include surrounding context). No change made.");

        config![field] = JsonValue.Create(ReplaceOnce(text, oldString, newString));
        return Finalize(root!, 1, $"Edited field '{field}' on node '{nodeId}'.");
    }

    /// <summary>
    /// Overwrite a node config field wholesale with <paramref name="value"/> (the
    /// intentional replace-all path). The field is created if absent. The value is
    /// stored as plain text; the server owns the JSON encoding.
    /// </summary>
    public static LoopEditResult SetNodeField(string document, string nodeId, string field, string value)
    {
        if (string.IsNullOrWhiteSpace(field))
            return LoopEditResult.MatchFailure(0, "field must not be empty.");

        if (!TryParseRoot(document, out var root, out var parseError))
            return LoopEditResult.MatchFailure(0, parseError!);

        var node = FindNode(root, nodeId);
        if (node == null)
            return LoopEditResult.MatchFailure(0, $"No node with id '{nodeId}' exists in the current loop.");
        if (node["config"] is not JsonObject config)
            return LoopEditResult.MatchFailure(0, $"Node '{nodeId}' has no config object to edit.");

        config[field] = JsonValue.Create(value);
        return Finalize(root, 1, $"Set field '{field}' on node '{nodeId}'.");
    }

    /// <summary>
    /// Replace a unique occurrence of <paramref name="oldString"/> in the raw JSON
    /// document text — the escape hatch for structural nudges (edges, ids, node
    /// scaffolding) a field edit can't reach. Zero or multiple matches change
    /// nothing. The result is still re-validated before it is returned.
    /// </summary>
    public static LoopEditResult EditFile(string document, string oldString, string newString)
    {
        if (string.IsNullOrEmpty(oldString))
            return LoopEditResult.MatchFailure(0, "old_string must not be empty.");

        var count = CountOccurrences(document, oldString);
        if (count == 0)
            return LoopEditResult.MatchFailure(0, "old_string was not found in the loop document; no change made.");
        if (count > 1)
            return LoopEditResult.MatchFailure(count,
                $"old_string matches {count} times in the loop document; make it unique (include surrounding context). No change made.");

        var candidate = ReplaceOnce(document, oldString, newString);
        var validationErrors = Validate(candidate, out var parseError);
        if (parseError != null)
            return LoopEditResult.MatchFailure(1, $"The edit produced invalid JSON: {parseError}. No change made.");
        if (validationErrors.Count > 0)
            return LoopEditResult.Invalid(1, validationErrors);

        return LoopEditResult.Success(1, candidate, "Edited the raw loop document.");
    }

    /// <summary>
    /// Validate and accept a whole-document replacement (the <c>update_current_loop</c>
    /// escape hatch, retrofitted with the same synchronous ack). A document that
    /// fails graph validation is rejected up front and the canvas is left untouched.
    /// </summary>
    public static LoopEditResult ReplaceDocument(string document)
    {
        var validationErrors = Validate(document, out var parseError);
        if (parseError != null)
            return LoopEditResult.MatchFailure(0, $"document is not valid JSON: {parseError}.");
        if (validationErrors.Count > 0)
            return LoopEditResult.Invalid(1, validationErrors);

        return LoopEditResult.Success(1, document, "Replaced the full loop document.");
    }

    // -- internals -------------------------------------------------------------

    /// <summary>
    /// Re-serialize a mutated document DOM, validate it, and turn the outcome into a
    /// <see cref="LoopEditResult"/>. Shared tail of the JsonNode-mutating surfaces
    /// (field edit/set) so reject-on-invalid behaves identically for all of them.
    /// </summary>
    private static LoopEditResult Finalize(JsonObject root, int matchCount, string summary)
    {
        var candidate = root.ToJsonString(OutputOptions);
        var validationErrors = Validate(candidate, out var parseError);
        // A DOM we just mutated always re-parses, so a parse error here would be a
        // bug rather than bad input; surface it as a match failure rather than throw.
        if (parseError != null)
            return LoopEditResult.MatchFailure(matchCount, $"The edit produced invalid JSON: {parseError}. No change made.");
        if (validationErrors.Count > 0)
            return LoopEditResult.Invalid(matchCount, validationErrors);

        return LoopEditResult.Success(matchCount, candidate, summary);
    }

    private static bool TryResolveField(
        string document, string nodeId, string field,
        out JsonObject? root, out JsonObject? config, out JsonNode? current, out string? error)
    {
        root = null; config = null; current = null; error = null;

        if (string.IsNullOrWhiteSpace(field))
        {
            error = "field must not be empty.";
            return false;
        }
        if (!TryParseRoot(document, out var parsed, out var parseError))
        {
            error = parseError;
            return false;
        }

        var node = FindNode(parsed, nodeId);
        if (node == null)
        {
            error = $"No node with id '{nodeId}' exists in the current loop.";
            return false;
        }
        if (node["config"] is not JsonObject cfg)
        {
            error = $"Node '{nodeId}' has no config object to edit.";
            return false;
        }
        if (!cfg.TryGetPropertyValue(field, out var value))
        {
            error = $"Field '{field}' does not exist on node '{nodeId}'.";
            return false;
        }

        root = parsed;
        config = cfg;
        current = value;
        return true;
    }

    private static JsonObject? FindNode(JsonObject root, string nodeId)
    {
        if (root["nodes"] is not JsonArray nodes) return null;
        foreach (var candidate in nodes)
        {
            if (candidate is JsonObject node && (string?)node["id"] == nodeId)
                return node;
        }
        return null;
    }

    private static bool TryParseRoot(string document, out JsonObject root, out string? error)
    {
        root = null!;
        try
        {
            if (JsonNode.Parse(document) is JsonObject obj)
            {
                root = obj;
                error = null;
                return true;
            }
            error = "The loop document is not a JSON object.";
            return false;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Run <see cref="LoopTemplateValidator"/> over a candidate document by parsing
    /// its <c>nodes</c>/<c>edges</c> into the graph DTO the validator expects. A
    /// parse failure is reported through <paramref name="parseError"/> (distinct from
    /// graph-validation errors) so callers can tell "not JSON" from "graph is wrong".
    /// </summary>
    private static IReadOnlyList<string> Validate(string document, out string? parseError)
    {
        parseError = null;
        LoopDocumentShape? shape;
        try
        {
            shape = JsonSerializer.Deserialize<LoopDocumentShape>(document, GraphParseOptions);
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
            return Array.Empty<string>();
        }

        if (shape == null)
        {
            parseError = "The loop document is empty.";
            return Array.Empty<string>();
        }

        var graph = new LoopTemplateGraph(
            Guid.Empty,
            shape.Nodes ?? new List<LoopNodeDto>(),
            shape.Edges ?? new List<LoopNodeEdgeDto>());
        return LoopTemplateValidator.Validate(graph);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReplaceOnce(string haystack, string needle, string replacement)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        return index < 0
            ? haystack
            : haystack[..index] + replacement + haystack[(index + needle.Length)..];
    }

    /// <summary>The subset of an <c>ild-loop-template/v1</c> document the validator needs.</summary>
    private sealed class LoopDocumentShape
    {
        public List<LoopNodeDto>? Nodes { get; set; }
        public List<LoopNodeEdgeDto>? Edges { get; set; }
    }
}
