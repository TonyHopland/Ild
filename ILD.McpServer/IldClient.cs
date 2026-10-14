using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;

namespace ILD.McpServer;

/// <summary>Where the server calls the ILD API, with which token, and for which run or chat.</summary>
public sealed record IldClientOptions(string ApiUrl, string ApiToken, string? LoopRunId, string? ChatSessionId = null)
{
    /// <summary>Names a JSON object holding the settings, for an agent CLI that cannot set a server's environment.</summary>
    public const string EnvFileVariable = "ILD_MCP_ENV_FILE";

    private const string DefaultApiUrl = "http://localhost:5000";

    /// <summary>
    /// The settings from the JSON object <see cref="EnvFileVariable"/> names, or,
    /// when it is unset or the file cannot be read as a JSON object, from the
    /// environment variables of the same names. A file that cannot be used is
    /// reported on <paramref name="log"/>, which must not be stdout: that carries
    /// the MCP protocol.
    /// </summary>
    public static IldClientOptions Resolve(Func<string, string?> getEnvironmentVariable, TextWriter log)
    {
        var file = getEnvironmentVariable(EnvFileVariable);
        if (!string.IsNullOrEmpty(file))
        {
            if (ReadSettingsFile(file, out var problem) is { } settings)
                return FromSettings(name => settings.GetValueOrDefault(name));
            log.WriteLine($"ild-mcp-server: ignoring {EnvFileVariable} {file}, which {problem}; using the environment.");
        }

        return FromSettings(getEnvironmentVariable);
    }

    private static IldClientOptions FromSettings(Func<string, string?> get)
        => new(
            get("ILD_API_URL") ?? DefaultApiUrl,
            get("ILD_API_TOKEN") ?? "",
            get("ILD_LOOP_RUN_ID"),
            get("ILD_CHAT_SESSION_ID"));

    private static Dictionary<string, string>? ReadSettingsFile(string path, out string problem)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = "cannot be read";
            return null;
        }
        catch (JsonException)
        {
            problem = "is not valid JSON";
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = "is not a JSON object";
                return null;
            }

            problem = "";
            var settings = new Dictionary<string, string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    settings[property.Name] = property.Value.GetString()!;
            }
            return settings;
        }
    }
}

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
