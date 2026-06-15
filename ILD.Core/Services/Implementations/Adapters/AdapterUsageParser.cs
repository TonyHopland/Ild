using System.Text.Json;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Extracts token/cost accounting from the JSON event streams the CLI agent
/// adapters already read. Each supported CLI reports usage in its own shape:
/// <list type="bullet">
///   <item>claude-code: a terminal <c>result</c> event with a <c>usage</c>
///   object (<c>input_tokens</c>/<c>output_tokens</c>, plus cache token fields)
///   and <c>total_cost_usd</c>.</item>
///   <item>opencode: assistant/step events with a <c>tokens</c> object
///   (<c>input</c>/<c>output</c>, plus a nested <c>cache</c>) and a <c>cost</c>.</item>
///   <item>pi: a <c>usage</c> object on its message/turn-end events (no cost).</item>
/// </list>
/// The parser is tolerant: it walks each JSON line and keeps the last usage
/// object and cost it sees, so a cumulative final event wins over earlier
/// partial ones. Returns <c>null</c> when the stream carries no usage at all.
/// </summary>
public static class AdapterUsageParser
{
    public static TokenUsage? Parse(string? rawStdout)
    {
        if (string.IsNullOrWhiteSpace(rawStdout)) return null;

        var state = new ParseState();
        foreach (var rawLine in rawStdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
                Walk(doc.RootElement, ref state);
        }

        if (!state.SawTokens && state.Cost is null) return null;

        var cost = state.TotalCostUsd ?? state.GenericCost;
        return new TokenUsage(state.InputTokens, state.OutputTokens, cost);
    }

    private struct ParseState
    {
        public long InputTokens;
        public long OutputTokens;
        public bool SawTokens;
        public decimal? TotalCostUsd;
        public decimal? GenericCost;
        public readonly decimal? Cost => TotalCostUsd ?? GenericCost;
    }

    private static void Walk(JsonElement element, ref ParseState state)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && string.Equals(prop.Name, "usage", StringComparison.OrdinalIgnoreCase))
                    CaptureUsageObject(prop.Value, ref state);
                else if (prop.Value.ValueKind == JsonValueKind.Object
                    && string.Equals(prop.Name, "tokens", StringComparison.OrdinalIgnoreCase))
                    CaptureTokensObject(prop.Value, ref state);
                else if (string.Equals(prop.Name, "total_cost_usd", StringComparison.OrdinalIgnoreCase)
                    && TryGetDecimal(prop.Value, out var totalCost))
                    state.TotalCostUsd = totalCost;
                else if ((string.Equals(prop.Name, "cost", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(prop.Name, "costUSD", StringComparison.OrdinalIgnoreCase))
                    && TryGetDecimal(prop.Value, out var cost))
                    state.GenericCost = cost;

                // Recurse so nested events (e.g. usage carried under a "message"
                // or "result" wrapper) are still found.
                if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    Walk(prop.Value, ref state);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Walk(item, ref state);
        }
    }

    /// <summary>claude-code / pi shape: input_tokens + cache fields, output_tokens.</summary>
    private static void CaptureUsageObject(JsonElement usage, ref ParseState state)
    {
        var input = SumLongs(usage, "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens");
        var output = SumLongs(usage, "output_tokens");
        if (input == 0 && output == 0) return;
        state.InputTokens = input;
        state.OutputTokens = output;
        state.SawTokens = true;
    }

    /// <summary>opencode shape: { input, output, cache: { read, write } }.</summary>
    private static void CaptureTokensObject(JsonElement tokens, ref ParseState state)
    {
        var input = SumLongs(tokens, "input");
        if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            input += SumLongs(cache, "read", "write");
        var output = SumLongs(tokens, "output");
        if (input == 0 && output == 0) return;
        state.InputTokens = input;
        state.OutputTokens = output;
        state.SawTokens = true;
    }

    private static long SumLongs(JsonElement obj, params string[] names)
    {
        long total = 0;
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var n)
                && n > 0)
                total += n;
        }
        return total;
    }

    private static bool TryGetDecimal(JsonElement value, out decimal result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out result))
            return true;
        result = 0m;
        return false;
    }
}
