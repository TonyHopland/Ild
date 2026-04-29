using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations.Adapters;

public class OpenAiCompatibleAdapter : IAgentAdapter
{
    private static readonly Regex Placeholder = new(
        @"\{\{\s*([A-Za-z][A-Za-z0-9_.:/\\-]*)\s*\}\}", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public string Name => "OpenAI Compatible";
    public string[] SupportedProviderTypes => ["openai"];
    public ConfigFieldDescriptor[] ConfigSchema => new ConfigFieldDescriptor[]
    {
        new("model", ConfigFieldType.Text, "Model", true, "gpt-4o", "Model identifier for the provider"),
        new("baseUrl", ConfigFieldType.Text, "Base URL", true, "https://api.openai.com", "Base URL for the API endpoint"),
        new("apiKey", ConfigFieldType.Text, "API Key", true, null, "Authentication key for the provider"),
        new("temperature", ConfigFieldType.Number, "Temperature", false, 0.7, "Controls randomness in output"),
        new("maxTokens", ConfigFieldType.Number, "Max Tokens", false, 4096, "Maximum tokens in the response"),
    };

    public OpenAiCompatibleAdapter(HttpClient http)
    {
        _http = http;
    }

    public async Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext ctx)
    {
        var prompt = ctx.ExecutionCount == 1 ? ctx.InitialPrompt : ctx.LoopPrompt;
        var rendered = await RenderPromptAsync(prompt, ctx.RunContext);

        var (baseUrl, apiKey, model) = ResolveProviderSettings(ctx.Provider);

        var requestUri = baseUrl.TrimEnd('/') + "/chat/completions";
        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = rendered } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var resp = await _http.SendAsync(req, ctx.Cancel);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync(ctx.Cancel);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ctx.Cancel);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
            return NodeExecutionResult.Ok(content);
        }
        catch (Exception ex)
        {
            return NodeExecutionResult.Fail($"[ai-error] {ex.Message}");
        }
    }

    private static (string BaseUrl, string? ApiKey, string Model) ResolveProviderSettings(ILD.Data.Entities.AiProvider provider)
    {
        if (!string.IsNullOrEmpty(provider.Config))
        {
            try
            {
                using var doc = JsonDocument.Parse(provider.Config);
                var root = doc.RootElement;
                var baseUrl = root.GetProperty("baseUrl").GetString() ?? provider.BaseUrl;
                var apiKey = root.TryGetProperty("apiKey", out var ak) ? ak.GetString() : provider.ApiKey;
                var model = root.GetProperty("model").GetString() ?? provider.Model;
                return (baseUrl, apiKey, model);
            }
            catch { }
        }
        return (provider.BaseUrl, provider.ApiKey, provider.Model);
    }

    private static Task<string> RenderPromptAsync(string template, LoopRunContext context)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WorkItem.Title"] = context.WorkItemTitle,
            ["WorkItem.Description"] = context.WorkItemDescription,
            ["WorkTree.Diff"] = "",
            ["EventLog.Summary"] = string.Join("\n", context.EventLogSummary ?? new()),
            ["EventLog.LastN"] = string.Join("\n", (context.EventLogSummary ?? new()).TakeLast(10)),
            ["Node.Input"] = context.PreviousNodeOutput ?? "",
            ["PreviousNode.Output"] = context.PreviousNodeOutput ?? "",
        };

        var rendered = Placeholder.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var v)) return v ?? "";
            if (key.StartsWith("WorkTree.File:", StringComparison.OrdinalIgnoreCase))
            {
                var rel = key.Substring("WorkTree.File:".Length);
                var full = string.IsNullOrEmpty(context.WorktreePath) ? null : Path.Combine(context.WorktreePath, rel);
                return full != null && File.Exists(full) ? File.ReadAllText(full) : "";
            }
            return m.Value;
        });
        return Task.FromResult(rendered);
    }
}
