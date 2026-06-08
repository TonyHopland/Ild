using System.ComponentModel.DataAnnotations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace ILD.Api.Controllers;

/// <summary>
/// Global WorkItem server connection settings. Previously these lived on each
/// <see cref="ILD.Data.Entities.RemoteProvider"/>; they are now a single
/// app-wide configuration stored in the AppSettings table. The API key is
/// redacted on read, mirroring the RemoteProviders contract.
/// </summary>
[ApiController]
[Route("api/v1/workitemserver")]
public class WorkItemServerController : ControllerBase
{
    private readonly IAppSettingStore _store;

    public WorkItemServerController(IAppSettingStore store)
    {
        _store = store;
    }

    public sealed class WorkItemServerConfigRequest
    {
        [Url]
        [StringLength(1024)]
        public string? Url { get; set; }

        [StringLength(1024)]
        public string? ApiKey { get; set; }

        [Range(1, 86400)]
        public int PollIntervalSeconds { get; set; } = AppSettingKeys.DefaultPollIntervalSeconds;

        [Range(1, 3600)]
        public int GraceIntervalSeconds { get; set; } = AppSettingKeys.DefaultGraceIntervalSeconds;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var url = (await _store.GetByKeyAsync(AppSettingKeys.WorkItemServerUrl, ct))?.Value;
        var apiKey = (await _store.GetByKeyAsync(AppSettingKeys.WorkItemServerApiKey, ct))?.Value;
        var pollRaw = (await _store.GetByKeyAsync(AppSettingKeys.WorkItemServerPollIntervalSeconds, ct))?.Value;
        var graceRaw = (await _store.GetByKeyAsync(AppSettingKeys.WorkItemServerGraceIntervalSeconds, ct))?.Value;

        return Ok(new
        {
            url = string.IsNullOrEmpty(url) ? null : url,
            apiKey = string.IsNullOrEmpty(apiKey) ? null : "***",
            hasApiKey = !string.IsNullOrEmpty(apiKey),
            pollIntervalSeconds = int.TryParse(pollRaw, out var p) ? p : AppSettingKeys.DefaultPollIntervalSeconds,
            graceIntervalSeconds = int.TryParse(graceRaw, out var g) ? g : AppSettingKeys.DefaultGraceIntervalSeconds,
        });
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] WorkItemServerConfigRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        await _store.UpsertAsync(AppSettingKeys.WorkItemServerUrl, request.Url ?? string.Empty, ct);

        // Only overwrite the secret when a new value is supplied so the UI can
        // submit without re-entering it.
        if (!string.IsNullOrEmpty(request.ApiKey))
            await _store.UpsertAsync(AppSettingKeys.WorkItemServerApiKey, request.ApiKey, ct);

        await _store.UpsertAsync(
            AppSettingKeys.WorkItemServerPollIntervalSeconds,
            request.PollIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ct);
        await _store.UpsertAsync(
            AppSettingKeys.WorkItemServerGraceIntervalSeconds,
            request.GraceIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ct);

        return await Get(ct);
    }
}
