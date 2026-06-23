using System.Net.Http.Json;
using System.Text.Json;

namespace ILD.McpServer;

public sealed record IldClientOptions(string ApiUrl, string ApiToken, string? LoopRunId, string? ChatSessionId = null);

/// <summary>
/// Thin HTTP wrapper for the ILD agent-scoped API surface (`/api/v1/agent/...`).
/// One instance per request is fine — it is registered as a typed HttpClient.
/// </summary>
public sealed class IldClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IldClientOptions _opts;

    public IldClient(HttpClient http, IldClientOptions opts)
    {
        _http = http;
        _opts = opts;
    }

    public string? LoopRunId => _opts.LoopRunId;

    public async Task<string> GetRawAsync(string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(path, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return EnsureJsonResult("GET", path, resp, body);
    }

    public async Task<string> PostJsonAsync(string path, object body, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return EnsureJsonResult("POST", path, resp, text);
    }

    public async Task<string> PutJsonAsync(string path, object body, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(path, body, JsonOptions, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return EnsureJsonResult("PUT", path, resp, text);
    }

    public async Task<string> DeleteAsync(string path, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(path, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        return EnsureJsonResult("DELETE", path, resp, text);
    }

    /// <summary>
    /// Validate an agent-API response and return its body, or throw a clear error.
    ///
    /// The ILD API ends its pipeline with <c>app.MapFallbackToFile("index.html")</c>,
    /// so a request no controller route matches — e.g. a brand-new
    /// <c>/api/v1/agent/...</c> endpoint hitting a server still running an older
    /// build — falls through to the SPA shell and returns <c>index.html</c> with
    /// <b>HTTP 200</b>, not a 404. Handing that HTML back to the model looks like
    /// data and derails the turn. The agent API only ever speaks JSON, so an HTML
    /// 200 always means "route missing / stale build" — never a payload. Surface it
    /// as an actionable error instead.
    /// </summary>
    public static string EnsureJsonResult(string method, string path, HttpResponseMessage resp, string body)
    {
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{method} {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}");

        if (LooksLikeHtml(resp, body))
            throw new HttpRequestException(
                $"{method} {path} returned an HTML page (HTTP {(int)resp.StatusCode}) instead of a JSON API "
                + "response. The agent API route is missing or the ILD server is running a stale build that "
                + "predates it — rebuild and restart the ILD API, then retry.");

        return body;
    }

    private static bool LooksLikeHtml(HttpResponseMessage resp, string body)
    {
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return true;

        var head = body.AsSpan().TrimStart();
        return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }
}
