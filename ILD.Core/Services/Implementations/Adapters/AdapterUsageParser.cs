using System.Text.Json;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Extracts token/cost accounting from the JSON event streams the CLI agent
/// adapters already read. Each supported CLI reports usage in its own shape:
/// <list type="bullet">
///   <item>claude-code: a terminal <c>result</c> event with a <c>usage</c>
///   object (<c>input_tokens</c>/<c>output_tokens</c>, plus cache token fields)
///   and <c>total_cost_usd</c>, the session's running total, read by <see cref="ParseClaude"/>.</item>
///   <item>opencode: a <c>step_finish</c> event per model call with a <c>tokens</c>
///   object (<c>input</c>/<c>output</c>, plus a nested <c>cache</c>) and a <c>cost</c>,
///   read by <see cref="ParseOpenCode"/>.</item>
///   <item>pi: a per-message <c>usage</c> object (<c>input</c>/<c>output</c>,
///   <c>cacheRead</c>/<c>cacheWrite</c>, <c>cost.total</c>) on every assistant
///   message and compaction, read by <see cref="ParsePi"/>.</item>
///   <item>codex: the session's running total on every <c>turn.completed</c> event, read by <see cref="ParseCodex"/>.</item>
/// </list>
/// <see cref="Parse"/> is tolerant: it walks each JSON line and keeps the last usage
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

    /// <summary>
    /// The usage of one <c>claude --output-format stream-json</c> run. Its tokens are
    /// the run's own, but <c>total_cost_usd</c> is the session's running total: on
    /// <c>--resume</c> Claude restores the cost it recorded in the transcript (see
    /// <see cref="ParseClaudeSessionCostBefore"/>) and counts on from it, so the run
    /// cost that total minus <paramref name="sessionCostBefore"/>. When the total is
    /// below the earlier one Claude did not count on from it, so the tokens are kept
    /// and the cost is unknown.
    /// </summary>
    public static TokenUsage? ParseClaude(string? rawStdout, decimal sessionCostBefore)
    {
        var usage = Parse(rawStdout);
        if (usage?.CostUsd is not { } total) return usage;
        return usage with { CostUsd = total >= sessionCostBefore ? total - sessionCostBefore : null };
    }

    /// <summary>
    /// The cost a Claude session had reached, read from its transcript before a run
    /// resumes it: the <c>totalCostUSD</c> of the last valid <c>cost-state</c> line
    /// of <paramref name="sessionId"/>, which Claude appends after every run and
    /// restores on <c>--resume</c>. Zero when there is none, as Claude then restores
    /// nothing; so should Claude change this ledger, its totals are recorded as they are.
    /// </summary>
    public static decimal ParseClaudeSessionCostBefore(string? transcriptJsonl, string sessionId)
    {
        var before = 0m;
        foreach (var rawLine in (transcriptJsonl ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && StringProperty(root, "type") == "cost-state"
                    && StringProperty(root, "sessionId") == sessionId
                    && root.TryGetProperty("totalCostUSD", out var total)
                    && TryGetDecimal(total, out var cost)
                    && cost >= 0)
                    before = cost;
            }
        }
        return before;
    }

    /// <summary>
    /// Sum the usage of every <c>step_finish</c> in an <c>opencode run --format json</c>
    /// stream. Each step is one model call and reports only that call's tokens and
    /// cost, so a turn of several steps used their sum. The cost is null when no
    /// step reported one.
    /// </summary>
    public static TokenUsage? ParseOpenCode(string? rawStdout)
    {
        long input = 0, output = 0;
        decimal? cost = null;
        foreach (var rawLine in (rawStdout ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || StringProperty(root, "type") != "step_finish"
                    || ObjectProperty(root, "part") is not { } part)
                    continue;

                if (ObjectProperty(part, "tokens") is { } tokens)
                {
                    input += SumLongs(tokens, "input");
                    if (ObjectProperty(tokens, "cache") is { } cache)
                        input += SumLongs(cache, "read", "write");
                    output += SumLongs(tokens, "output");
                }
                if (part.TryGetProperty("cost", out var stepCost) && TryGetDecimal(stepCost, out var callCost))
                    cost = (cost ?? 0m) + callCost;
            }
        }

        if (input == 0 && output == 0 && cost is null) return null;
        return new TokenUsage(input, output, cost);
    }

    /// <summary>
    /// Sum the usage of every assistant <c>message_end</c> and every successful
    /// <c>compaction_end</c> (its summarizing call) in a <c>pi --mode json</c> stream.
    /// Pi reports usage per message and repeats it on <c>message_update</c>,
    /// <c>turn_end</c> and <c>agent_end</c>, so only <c>message_end</c> is counted.
    /// Pi reports a zero cost for a model it has no price for, so a zero total is
    /// no cost at all.
    /// </summary>
    public static TokenUsage? ParsePi(string? rawStdout)
    {
        if (string.IsNullOrWhiteSpace(rawStdout)) return null;

        long input = 0, output = 0;
        decimal cost = 0m;
        foreach (var rawLine in rawStdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (PiCountedUsage(doc.RootElement) is not { } usage)
                    continue;

                input += SumLongs(usage, "input", "cacheRead", "cacheWrite");
                output += SumLongs(usage, "output");
                if (usage.TryGetProperty("cost", out var costObject) && costObject.ValueKind == JsonValueKind.Object
                    && costObject.TryGetProperty("total", out var total) && TryGetDecimal(total, out var callCost))
                    cost += callCost;
            }
        }

        if (input == 0 && output == 0) return null;
        return new TokenUsage(input, output, cost > 0 ? cost : null);
    }

    /// <summary>
    /// The usage of one <c>codex exec --json</c> run. Each <c>turn.completed</c>
    /// carries the session's running total rather than the turn's own figures (a
    /// resumed session counts on from where it stopped), so the run used the last
    /// total minus <paramref name="sessionTotalBefore"/>, the total the session
    /// had when the run started (see <see cref="ParseCodexTotalBeforeLastTurn"/>). Its
    /// <c>cached_input_tokens</c> and <c>reasoning_output_tokens</c> are parts of
    /// <c>input_tokens</c> and <c>output_tokens</c>, so they are not added again.
    /// Codex reports no cost. Returns <c>null</c> when no turn reported its usage,
    /// or when the total is below the session's earlier one: then codex did not
    /// count on from it, and what this run used is unknown.
    /// </summary>
    public static TokenUsage? ParseCodex(string? rawStdout, TokenUsage sessionTotalBefore)
    {
        TokenUsage? total = null;
        foreach (var rawLine in (rawStdout ?? "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && StringProperty(root, "type") == "turn.completed"
                    && ObjectProperty(root, "usage") is { } usage)
                    total = new TokenUsage(SumLongs(usage, "input_tokens"), SumLongs(usage, "output_tokens"), null);
            }
        }

        if (total is null
            || total.InputTokens < sessionTotalBefore.InputTokens
            || total.OutputTokens < sessionTotalBefore.OutputTokens)
            return null;
        return new TokenUsage(
            total.InputTokens - sessionTotalBefore.InputTokens,
            total.OutputTokens - sessionTotalBefore.OutputTokens,
            null);
    }

    /// <summary>
    /// The running total a codex session had reached before its last turn, read
    /// from its rollout file after that turn: the <c>total_token_usage</c> of the
    /// last <c>token_count</c> event before the last <c>task_started</c>, which
    /// opens every turn (zero when the turn was the session's first). Null when
    /// there is no rollout or no turn in it.
    /// </summary>
    public static TokenUsage? ParseCodexTotalBeforeLastTurn(string? rolloutJsonl)
    {
        if (string.IsNullOrWhiteSpace(rolloutJsonl)) return null;

        var total = new TokenUsage(0, 0, null);
        TokenUsage? beforeLastTurn = null;
        foreach (var rawLine in rolloutJsonl.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || StringProperty(root, "type") != "event_msg"
                    || ObjectProperty(root, "payload") is not { } payload)
                    continue;

                switch (StringProperty(payload, "type"))
                {
                    case "task_started":
                        beforeLastTurn = total;
                        break;
                    case "token_count" when ObjectProperty(payload, "info") is { } info
                                            && ObjectProperty(info, "total_token_usage") is { } usage:
                        total = new TokenUsage(SumLongs(usage, "input_tokens"), SumLongs(usage, "output_tokens"), null);
                        break;
                }
            }
        }
        return beforeLastTurn;
    }

    private static string? StringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static JsonElement? ObjectProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static JsonElement? PiCountedUsage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return null;

        JsonElement owner;
        switch (type.GetString())
        {
            case "message_end":
                if (!root.TryGetProperty("message", out owner) || owner.ValueKind != JsonValueKind.Object
                    || !owner.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                    || role.GetString() != "assistant")
                    return null;
                break;
            case "compaction_end":
                if (!root.TryGetProperty("result", out owner) || owner.ValueKind != JsonValueKind.Object)
                    return null;
                break;
            default:
                return null;
        }

        return owner.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object ? usage : null;
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

    /// <summary>claude-code shape: input_tokens + cache fields, output_tokens.</summary>
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
