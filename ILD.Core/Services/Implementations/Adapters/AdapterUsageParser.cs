using System.Text.Json;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Extracts token/cost accounting from the JSON event streams the CLI agent
/// adapters already read. Each supported CLI reports usage in its own shape:
/// <list type="bullet">
///   <item>claude-code: a terminal <c>result</c> event with a <c>usage</c>
///   object (<c>input_tokens</c>/<c>output_tokens</c>, plus cache token fields)
///   and <c>total_cost_usd</c>, the session's running total (a launch of several turns has one such event per turn), read by <see cref="ParseClaude"/>.</item>
///   <item>opencode: a <c>step_finish</c> event per model call with a <c>tokens</c>
///   object (<c>input</c>/<c>output</c>, plus a nested <c>cache</c>) and a <c>cost</c>,
///   read by <see cref="ParseOpenCode"/>.</item>
///   <item>pi: a per-message <c>usage</c> object (<c>input</c>/<c>output</c>,
///   <c>cacheRead</c>/<c>cacheWrite</c>, <c>cost.total</c>) on every assistant
///   message and compaction, read by <see cref="ParsePi"/>.</item>
///   <item>codex: the session's running total on every <c>turn.completed</c> event, read by <see cref="ParseCodex"/>.</item>
/// </list>
/// </summary>
public static class AdapterUsageParser
{
    /// <summary>
    /// The usage of one <c>claude --output-format stream-json</c> launch, read from its <c>result</c>
    /// events. There is one per turn, since the CLI waits for background work and gives the agent
    /// another turn, so the number of results is the number of turns.
    /// <para>
    /// Tokens come from the last <c>modelUsage</c>: it is cumulative over the launch (each result's
    /// figures include the earlier turns') and covers sub-agents, which each result's <c>usage</c>
    /// leaves out. It is summed over its model ids, which are also the model. This was confirmed on
    /// the real streams in <c>Fixtures/claude-streams</c> (see its README): the last <c>modelUsage</c>
    /// equals the sum of the per-turn <c>usage</c> objects, plus the sub-agent's tokens in
    /// <c>background-subagent</c>. No capture of a resumed launch exists, so <see cref="SpansEarlierLaunches"/>
    /// guards against <c>modelUsage</c> carrying the session's earlier launches.
    /// Without a usable <c>modelUsage</c>, the tokens are the sum of the <c>usage</c> objects.
    /// </para>
    /// <para>
    /// <c>total_cost_usd</c> is a running total, so the last <c>result</c> holds the whole launch's.
    /// It is also the session's running total: on <c>--resume</c> Claude restores the cost it
    /// recorded in the transcript (see <see cref="ParseClaudeSessionCostBefore"/>) and counts on
    /// from it, so what the run cost is that total minus <paramref name="sessionCostBefore"/>.
    /// When the total is below the earlier one Claude did not count on from it, so the tokens are
    /// kept and the cost is unknown.
    /// </para>
    /// Returns <c>null</c> when no result reported usage or a cost.
    /// </summary>
    public static TokenUsage? ParseClaude(string? rawStdout, decimal sessionCostBefore)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        int turns = 0;
        decimal? total = null;
        ModelTotals? models = null;
        ForEachJsonObject(rawStdout, root =>
        {
            if (StringProperty(root, "type") != "result") return;

            turns++;
            if (ObjectProperty(root, "usage") is { } usage)
            {
                input += SumLongs(usage, "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens");
                output += SumLongs(usage, "output_tokens");
                cacheRead += SumLongs(usage, "cache_read_input_tokens");
                cacheWrite += SumLongs(usage, "cache_creation_input_tokens");
            }
            if (ObjectProperty(root, "modelUsage") is { } modelUsage && ReadModelTotals(modelUsage) is { } read)
                models = read;
            if (root.TryGetProperty("total_cost_usd", out var cost) && TryGetDecimal(cost, out var resultTotal))
                total = resultTotal;
        });

        if (turns == 0) return null;
        var launchCost = total >= sessionCostBefore ? total - sessionCostBefore : null;

        // modelUsage counts the whole launch and the sub-agents the main agent ran, which `usage` leaves
        // out. It is used unless its cost shows it carries the session's earlier launches too.
        string? model = null;
        if (models is { } m && m.Input + m.Output + m.CacheRead + m.CacheWrite > 0 && !SpansEarlierLaunches(m.CostUsd, launchCost, sessionCostBefore))
        {
            input = m.Input + m.CacheRead + m.CacheWrite;
            output = m.Output;
            cacheRead = m.CacheRead;
            cacheWrite = m.CacheWrite;
            model = m.Model;
        }

        if (input == 0 && output == 0 && total is null) return null;
        return new TokenUsage(input, output, launchCost, CacheReadTokens: cacheRead, CacheWriteTokens: cacheWrite, Turns: turns, Model: model);
    }

    /// <summary>Rounding slack, in USD and as a fraction of the launch's cost, between <c>modelUsage</c>'s cost and the launch's.</summary>
    private const decimal ModelCostToleranceUsd = 0.001m;
    private const decimal ModelCostToleranceFraction = 0.01m;

    /// <summary>
    /// Whether a resumed launch's <c>modelUsage</c> costs clearly more than the launch did, so it counts the
    /// session's earlier launches too and its tokens are not this launch's. False when nothing shows it.
    /// </summary>
    private static bool SpansEarlierLaunches(decimal modelCostUsd, decimal? launchCost, decimal sessionCostBefore)
        => launchCost is { } spent
           && sessionCostBefore > 0
           && modelCostUsd - spent > Math.Max(ModelCostToleranceUsd, spent * ModelCostToleranceFraction);

    private readonly record struct ModelTotals(long Input, long Output, long CacheRead, long CacheWrite, decimal CostUsd, string? Model);

    /// <summary>The <c>modelUsage</c> of a Claude <c>result</c>, summed over its model ids; null when it names none.</summary>
    private static ModelTotals? ReadModelTotals(JsonElement modelUsage)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        var cost = 0m;
        var names = new List<string>();
        foreach (var entry in modelUsage.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            names.Add(entry.Name);
            input += SumLongs(entry.Value, "inputTokens");
            output += SumLongs(entry.Value, "outputTokens");
            cacheRead += SumLongs(entry.Value, "cacheReadInputTokens");
            cacheWrite += SumLongs(entry.Value, "cacheCreationInputTokens");
            if (entry.Value.TryGetProperty("costUSD", out var c) && TryGetDecimal(c, out var modelCost))
                cost += modelCost;
        }
        return names.Count == 0 ? null : new ModelTotals(input, output, cacheRead, cacheWrite, cost, string.Join(", ", names));
    }

    /// <summary>
    /// The cost a Claude session had reached, read from its transcript before a run
    /// resumes it: the <c>totalCostUSD</c> of the last valid <c>cost-state</c> line
    /// of <paramref name="sessionId"/>, which Claude appends after every run and
    /// restores on <c>--resume</c>. Zero when there is none, as Claude then restores
    /// nothing; so should Claude change this ledger, its totals are recorded as they are.
    /// </summary>
    public static decimal ParseClaudeSessionCostBefore(TextReader transcriptJsonl, string sessionId)
    {
        var before = 0m;
        ForEachJsonObject(transcriptJsonl, root =>
        {
            if (StringProperty(root, "type") == "cost-state"
                && StringProperty(root, "sessionId") == sessionId
                && root.TryGetProperty("totalCostUSD", out var total)
                && TryGetDecimal(total, out var cost)
                && cost >= 0)
                before = cost;
        }, lineMustContain: "cost-state");
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
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        decimal? cost = null;
        ForEachJsonObject(rawStdout, root =>
        {
            if (StringProperty(root, "type") != "step_finish"
                || ObjectProperty(root, "part") is not { } part)
                return;

            if (ObjectProperty(part, "tokens") is { } tokens)
            {
                input += SumLongs(tokens, "input");
                if (ObjectProperty(tokens, "cache") is { } cache)
                {
                    cacheRead += SumLongs(cache, "read");
                    cacheWrite += SumLongs(cache, "write");
                }
                output += SumLongs(tokens, "output");
            }
            if (part.TryGetProperty("cost", out var stepCost) && TryGetDecimal(stepCost, out var callCost))
                cost = (cost ?? 0m) + callCost;
        });

        input += cacheRead + cacheWrite;
        if (input == 0 && output == 0 && cost is null) return null;
        return new TokenUsage(input, output, cost, CacheReadTokens: cacheRead, CacheWriteTokens: cacheWrite);
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
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        decimal cost = 0m;
        ForEachJsonObject(rawStdout, root =>
        {
            if (PiCountedUsage(root) is not { } usage)
                return;

            input += SumLongs(usage, "input", "cacheRead", "cacheWrite");
            cacheRead += SumLongs(usage, "cacheRead");
            cacheWrite += SumLongs(usage, "cacheWrite");
            output += SumLongs(usage, "output");
            if (usage.TryGetProperty("cost", out var costObject) && costObject.ValueKind == JsonValueKind.Object
                && costObject.TryGetProperty("total", out var total) && TryGetDecimal(total, out var callCost))
                cost += callCost;
        });

        if (input == 0 && output == 0) return null;
        return new TokenUsage(input, output, cost > 0 ? cost : null, CacheReadTokens: cacheRead, CacheWriteTokens: cacheWrite);
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
        ForEachJsonObject(rawStdout, root =>
        {
            if (StringProperty(root, "type") == "turn.completed"
                && ObjectProperty(root, "usage") is { } usage)
                total = new TokenUsage(SumLongs(usage, "input_tokens"), SumLongs(usage, "output_tokens"), null);
        });

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
    public static TokenUsage? ParseCodexTotalBeforeLastTurn(TextReader rolloutJsonl)
    {
        var total = new TokenUsage(0, 0, null);
        TokenUsage? beforeLastTurn = null;
        ForEachJsonObject(rolloutJsonl, root =>
        {
            if (StringProperty(root, "type") != "event_msg"
                || ObjectProperty(root, "payload") is not { } payload)
                return;

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
        });
        return beforeLastTurn;
    }

    /// <summary>
    /// Calls <paramref name="onObject"/> with each line of <paramref name="jsonl"/> that
    /// parses as a JSON object, skipping blank and unparsable lines and, when
    /// <paramref name="lineMustContain"/> is given, lines without that text.
    /// </summary>
    private static void ForEachJsonObject(string? jsonl, Action<JsonElement> onObject, string? lineMustContain = null)
        => ForEachJsonObject(new StringReader(jsonl ?? ""), onObject, lineMustContain);

    /// <summary>As above, reading the lines one at a time so a long transcript is never held whole.</summary>
    private static void ForEachJsonObject(TextReader jsonl, Action<JsonElement> onObject, string? lineMustContain = null)
    {
        while (jsonl.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (lineMustContain is not null && !line.Contains(lineMustContain, StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    onObject(doc.RootElement);
            }
        }
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
