using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.Extensions.Http;

namespace ILD.Core.Services.Implementations.Adapters;

public class OpenAiCompatibleAdapter : IAgentAdapter
{
    private static readonly IPromptTemplateResolver Resolver = new PromptTemplateResolver();

    private readonly IHttpClientFactory _factory;

    public string Name => "OpenAI Compatible";
    public string[] SupportedProviderTypes => ["openai"];
    public ConfigFieldDescriptor[] ConfigSchema => new ConfigFieldDescriptor[]
    {
        new("temperature", ConfigFieldType.Number, "Temperature", false, 0.7, "Controls randomness in output"),
        new("maxTokens", ConfigFieldType.Number, "Max Tokens", false, 4096, "Maximum tokens in the response"),
    };

    public OpenAiCompatibleAdapter(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        var prompt = ctx.ExecutionCount == 1 ? ctx.InitialPrompt : ctx.LoopPrompt;
        var rendered = await RenderPromptAsync(prompt, ctx.RunContext);

        var (baseUrl, apiKey, model, providerTemp, providerMaxTokens) = ResolveProviderSettings(ctx.Provider);

        var temperature = ResolveDouble(ctx.AdapterConfig, "temperature", providerTemp);
        var maxTokens = ResolveInt(ctx.AdapterConfig, "maxTokens", providerMaxTokens);

        var requestUri = baseUrl.TrimEnd('/') + "/chat/completions";
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "user", content = rendered } },
            ["stream"] = true,
            ["temperature"] = temperature,
            ["maxTokens"] = maxTokens,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var client = _factory.CreateClient();
            using var resp = await client.SendAsync(req, ctx.Cancel);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync(ctx.Cancel);
            var (content, _) = await ReadSseStreamAsync(stream, ctx.ProgressCallback, ctx.Cancel);
            return NodeExecutionResult.Ok(content, rendered, sessionId: null, ctx.IncomingSessionId);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[ai-error] {ex.Message}");
        }
    }

    private static (string BaseUrl, string? ApiKey, string Model, double Temperature, int MaxTokens) ResolveProviderSettings(ILD.Data.Entities.AiProvider provider)
    {
        var temperature = 0.7;
        var maxTokens = 4096;
        var baseUrl = provider.BaseUrl;
        var apiKey = provider.ApiKey;
        var model = provider.Model;

        if (!string.IsNullOrEmpty(provider.Config))
        {
            try
            {
                using var doc = JsonDocument.Parse(provider.Config);
                var root = doc.RootElement;
                if (root.TryGetProperty("baseUrl", out var bu) && bu.ValueKind == JsonValueKind.String)
                    baseUrl = bu.GetString() ?? baseUrl;
                if (root.TryGetProperty("apiKey", out var ak))
                    apiKey = ak.GetString();
                if (root.TryGetProperty("model", out var ml) && ml.ValueKind == JsonValueKind.String)
                    model = ml.GetString() ?? model;
                if (root.TryGetProperty("temperature", out var temp) && temp.ValueKind == JsonValueKind.Number)
                    temperature = temp.GetDouble();
                if (root.TryGetProperty("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number)
                    maxTokens = (int)mt.GetDouble();
            }
            catch { }
        }
        return (baseUrl, apiKey, model, temperature, maxTokens);
    }

    private static Task<string> RenderPromptAsync(string template, LoopRunContext context)
        => Task.FromResult(Resolver.Render(template, new PromptContext(
            WorkItemTitle: context.WorkItemTitle,
            WorkItemDescription: context.WorkItemDescription,
            PreviousNodeOutput: context.PreviousNodeOutput,
            EventLogSummary: context.EventLogSummary,
            WorktreePath: context.WorktreePath)));

    private static async Task<(string Content, int ChunkCount)> ReadSseStreamAsync(
        Stream stream,
        Func<string, Task>? progressCallback,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        int chunkCount = 0;
        using var reader = new System.IO.StreamReader(stream);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[done]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                    {
                        var text = contentProp.GetString() ?? "";
                        sb.Append(text);
                        if (progressCallback != null && !string.IsNullOrEmpty(text))
                        {
                            chunkCount++;
                            await progressCallback(text).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch { /* skip malformed SSE chunks */ }
        }

        return (sb.ToString(), chunkCount);
    }

    static double ResolveDouble(Dictionary<string, object?>? config, string key, double @default)
    {
        if (config != null && config.TryGetValue(key, out var v) && v is double d) return d;
        if (config != null && config.TryGetValue(key, out var v2) && v2 is float f) return f;
        if (config != null && config.TryGetValue(key, out var v3) && v3 is int i) return i;
        return @default;
    }

    static int ResolveInt(Dictionary<string, object?>? config, string key, int @default)
    {
        if (config != null && config.TryGetValue(key, out var v) && v is int i) return i;
        if (config != null && config.TryGetValue(key, out var v2) && v2 is double d) return (int)d;
        return @default;
    }
}
