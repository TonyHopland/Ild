using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.Backup;

namespace ILD.Core.Services.Remote;

/// <summary>
/// The WorkItem Server's backup endpoints. They are outside the work item contract (ADR-0029): a
/// tracker that is not the built-in server does not have them, which <see cref="GetInfoAsync"/>
/// reports as null. The archive is the server's own, unencrypted; encrypting it is ILD's.
/// </summary>
public interface IWorkItemServerBackupClient
{
    /// <summary>What the server's database is, or null when the server offers no backup.</summary>
    Task<ArchiveInfo?> GetInfoAsync(WorkItemServerOptions opts, CancellationToken ct);

    /// <summary>Hands <paramref name="consume"/> the archive as the server sends it.</summary>
    Task ExportAsync(WorkItemServerOptions opts, WorkItemActor actor, Func<Stream, Task> consume, CancellationToken ct);

    /// <summary>Replaces the server's data with <paramref name="archive"/>. A refusal is a <see cref="BackupRefusedException"/>.</summary>
    Task ImportAsync(WorkItemServerOptions opts, WorkItemActor actor, Stream archive, CancellationToken ct);
}

public sealed class WorkItemServerBackupClient(HttpClient http) : IWorkItemServerBackupClient
{
    // The client has no timeout of its own: an export or an import runs as long as the data is large.
    // Asking what the server is has to answer, or the page that asks never loads.
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ArchiveInfo?> GetInfoAsync(WorkItemServerOptions opts, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(InfoTimeout);
        try
        {
            using var request = Build(opts, HttpMethod.Get, "");
            using var response = await http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            await EnsureSuccessAsync(response, request, ct);
            return await response.Content.ReadFromJsonAsync<ArchiveInfo>(Json, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException($"The WorkItem Server did not answer within {InfoTimeout.TotalSeconds:0} seconds.");
        }
    }

    public async Task ExportAsync(WorkItemServerOptions opts, WorkItemActor actor, Func<Stream, Task> consume, CancellationToken ct)
    {
        using var request = Build(opts, HttpMethod.Post, "/export", actor);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, request, ct);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await consume(body);
    }

    public async Task ImportAsync(WorkItemServerOptions opts, WorkItemActor actor, Stream archive, CancellationToken ct)
    {
        using var request = Build(opts, HttpMethod.Post, "/import", actor);
        request.Content = new StreamContent(archive) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            throw new BackupRefusedException(await ReadErrorAsync(response, ct) ?? "The WorkItem Server refused the backup.");
        await EnsureSuccessAsync(response, request, ct);
    }

    private static HttpRequestMessage Build(WorkItemServerOptions opts, HttpMethod method, string path, WorkItemActor? actor = null)
    {
        var request = new HttpRequestMessage(method, $"{opts.BaseUrl.TrimEnd('/')}/maintenance/backup{path}");
        if (!string.IsNullOrEmpty(opts.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        if (actor is not null)
            request.Headers.Add(WorkItemActor.HeaderName, actor.ToHeaderValue());
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, HttpRequestMessage request, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = response.StatusCode == HttpStatusCode.Unauthorized
            ? " The configured API key is not one of that server's WORKITEM_API_KEYS."
            : await ReadErrorAsync(response, ct) is { } error ? $" {error}" : "";
        throw new HttpRequestException(
            $"{request.Method} {request.RequestUri!.AbsolutePath} was refused: {(int)response.StatusCode} {response.ReasonPhrase}.{detail}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
