using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.ModelGateway;

public sealed record CodexLogin(string AccessToken, string? AccountId);

/// <summary>
/// The Codex ChatGPT login in the credential store, kept fresh by ILD instead of by the CLI, because the
/// run's CLI only ever holds a placeholder. The refresh token is sent to the issuer from here and never
/// leaves ILD (ADR-0031).
/// </summary>
internal sealed class CodexLoginStore
{
    internal const string RefreshUrl = "https://auth.openai.com/oauth/token";
    private const string DefaultClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(8);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromHours(24);

    private readonly string _path;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _refreshNext;

    public CodexLoginStore(string path, HttpClient http, TimeProvider clock, ILogger logger)
    {
        _path = path;
        _http = http;
        _clock = clock;
        _logger = logger;
    }

    public void RefreshNext() => _refreshNext = true;

    public async Task<CodexLogin?> CurrentAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var root = Read();
            if (root?["tokens"] is not JsonObject tokens || tokens["access_token"]?.GetValue<string>() is not { Length: > 0 } access)
                return null;

            if (NeedsRefresh(root, access) && tokens["refresh_token"]?.GetValue<string>() is { Length: > 0 } refresh)
            {
                var renewed = await TryRefreshAsync(root, tokens, refresh, ct);
                if (renewed is not null) access = renewed;
            }
            return new CodexLogin(access, tokens["account_id"]?.GetValue<string>());
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool NeedsRefresh(JsonNode root, string access)
    {
        if (_refreshNext) return true;
        var now = _clock.GetUtcNow();
        if (DateTimeOffset.TryParse(root["last_refresh"]?.GetValue<string>(), out var last) && now - last > MaxAge) return true;
        return JwtExpiry(access) is { } expiry && expiry - now < ExpiryMargin;
    }

    private async Task<string?> TryRefreshAsync(JsonNode root, JsonObject tokens, string refresh, CancellationToken ct)
    {
        try
        {
            var clientId = tokens["oauth_provenance"]?["client_id"]?.GetValue<string>() ?? DefaultClientId;
            using var response = await _http.PostAsJsonAsync(RefreshUrl, new
            {
                client_id = clientId,
                grant_type = "refresh_token",
                refresh_token = refresh,
                scope = "openid profile email",
            }, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Refreshing the Codex login was refused with {Status}", (int)response.StatusCode);
                return null;
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var access = body?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(access)) return null;

            tokens["access_token"] = access;
            if (body?["refresh_token"]?.GetValue<string>() is { Length: > 0 } newRefresh) tokens["refresh_token"] = newRefresh;
            if (body?["id_token"]?.GetValue<string>() is { Length: > 0 } idToken) tokens["id_token"] = idToken;
            root["last_refresh"] = _clock.GetUtcNow().UtcDateTime.ToString("O");
            WriteAtomically(root);
            _refreshNext = false;
            return access;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Refreshing the Codex login failed: {Reason}", ex.Message);
            return null;
        }
    }

    private JsonNode? Read()
    {
        try { return File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private void WriteAtomically(JsonNode root)
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
                stream.Flush(true);
            }
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static DateTimeOffset? JwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var payload = JsonNode.Parse(Convert.FromBase64String(padded));
            return payload?["exp"] is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp.GetValue<long>()) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
