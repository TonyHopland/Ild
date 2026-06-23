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
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}");
        return body;
    }

    public async Task<string> PostJsonAsync(string path, object body, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(path, body, JsonOptions, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"POST {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {text}");
        return text;
    }

    public async Task<string> PutJsonAsync(string path, object body, CancellationToken ct = default)
    {
        using var resp = await _http.PutAsJsonAsync(path, body, JsonOptions, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"PUT {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {text}");
        return text;
    }

    public async Task<string> DeleteAsync(string path, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(path, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DELETE {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {text}");
        return text;
    }
}
