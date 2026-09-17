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
        => SendAsync("GET", path, () => _http.GetAsync(path, ct), ReadTextAsync, ct);

    /// <summary>
    /// A file the API serves, with the content type and name it served it under —
    /// the bytes, not their text, so an image survives the trip.
    /// </summary>
    public Task<(byte[] Content, string ContentType, string FileName)> GetBinaryAsync(string path, CancellationToken ct = default)
        => SendAsync("GET", path, () => _http.GetAsync(path, ct), ReadFileAsync, ct);

    public Task<string> PostJsonAsync(string path, object body, CancellationToken ct = default)
        => SendAsync("POST", path, () => _http.PostAsJsonAsync(path, body, JsonOptions, ct), ReadTextAsync, ct);

    public Task<string> PutJsonAsync(string path, object body, CancellationToken ct = default)
        => SendAsync("PUT", path, () => _http.PutAsJsonAsync(path, body, JsonOptions, ct), ReadTextAsync, ct);

    public Task<string> DeleteAsync(string path, CancellationToken ct = default)
        => SendAsync("DELETE", path, () => _http.DeleteAsync(path, ct), ReadTextAsync, ct);

    private static async Task<string> ReadTextAsync(HttpResponseMessage resp, CancellationToken ct)
        => await resp.Content.ReadAsStringAsync(ct);

    private static async Task<(byte[] Content, string ContentType, string FileName)> ReadFileAsync(
        HttpResponseMessage resp, CancellationToken ct)
    {
        var content = await resp.Content.ReadAsByteArrayAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
            ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "attachment";
        return (content, contentType, fileName);
    }

    /// <summary>
    /// Every failure leaves here as an <see cref="McpException"/>, because that is
    /// the only exception type the MCP host passes through to the agent: anything
    /// else is replaced with "An error occurred invoking '&lt;tool&gt;'", which
    /// cannot tell an expired token from a refused role from a server that is not
    /// listening. The message therefore has to carry the URL and the status itself.
    ///
    /// A failed response is always read as text, whatever the caller wanted: the
    /// body is the API's explanation and belongs in the message.
    /// </summary>
    private async Task<T> SendAsync<T>(
        string method,
        string path,
        Func<Task<HttpResponseMessage>> send,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        CancellationToken ct)
    {
        // Absolute, not the relative path the callers pass: which ILD instance
        // answered is half the diagnosis. A preview's MCP server calling the host
        // API instead of its own is a 401 that looks identical to an expired token
        // until you can see the address it went to.
        var url = $"{_http.BaseAddress}{path}";

        HttpResponseMessage resp;
        try
        {
            resp = await send();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up; nothing failed and there is nothing to report.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new McpException(
                $"{method} {url} could not reach the ILD API (ILD_API_URL={_opts.ApiUrl}): {ex.Message}");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new McpException(
                    $"{method} {url} failed: {(int)resp.StatusCode} {resp.ReasonPhrase} — {await resp.Content.ReadAsStringAsync(ct)}");
            return await read(resp, ct);
        }
    }
}
