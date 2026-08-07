using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;

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

    public Task<string> GetRawAsync(string path, CancellationToken ct = default)
        => SendAsync("GET", path, () => _http.GetAsync(path, ct), ct);

    public Task<string> PostJsonAsync(string path, object body, CancellationToken ct = default)
        => SendAsync("POST", path, () => _http.PostAsJsonAsync(path, body, JsonOptions, ct), ct);

    public Task<string> PutJsonAsync(string path, object body, CancellationToken ct = default)
        => SendAsync("PUT", path, () => _http.PutAsJsonAsync(path, body, JsonOptions, ct), ct);

    public Task<string> DeleteAsync(string path, CancellationToken ct = default)
        => SendAsync("DELETE", path, () => _http.DeleteAsync(path, ct), ct);

    /// <summary>
    /// Every failure leaves here as an <see cref="McpException"/>, because that is
    /// the only exception type the MCP host passes through to the agent: anything
    /// else is replaced with "An error occurred invoking '&lt;tool&gt;'", which
    /// cannot tell an expired token from a refused role from a server that is not
    /// listening. The message therefore has to carry the URL and the status itself.
    /// </summary>
    private async Task<string> SendAsync(
        string method, string path, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new McpException(
                $"{method} {_http.BaseAddress}{path} could not reach the ILD API " +
                $"(ILD_API_URL={_opts.ApiUrl}): {ex.Message}");
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new McpException($"{method} {path} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {body}");
            return body;
        }
    }
}
