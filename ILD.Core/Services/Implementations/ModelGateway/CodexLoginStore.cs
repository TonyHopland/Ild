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
    internal static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromHours(24);

    private enum WriteOutcome { Written, Failed, Superseded }

    private sealed record Stored(JsonNode Root, JsonObject Tokens, string Access, string? Refresh);

    private sealed record Pending(JsonNode Root, string FileRefresh);

    private readonly string _path;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _refreshNext;
    private Task? _inFlight;
    private Pending? _pending;
    private DateTimeOffset? _retryAfter;
    private TimeSpan _delay;
    private string? _failedFor;

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
            if (_inFlight is not null) await AwaitRefreshAsync(ct);
            FlushPending();

            var stored = Load();
            if (stored is null) return null;

            if (stored.Refresh is { Length: > 0 } refresh && NeedsRefresh(stored.Root, stored.Access))
            {
                _inFlight = RefreshAsync(stored.Root, stored.Tokens, refresh, _pending?.FileRefresh ?? refresh);
                await AwaitRefreshAsync(ct);
                stored = Load();
                if (stored is null) return null;
            }
            return new CodexLogin(stored.Access, stored.Tokens["account_id"]?.GetValue<string>());
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task AwaitRefreshAsync(CancellationToken ct)
    {
        var task = _inFlight!;
        try
        {
            await task.WaitAsync(ct);
        }
        finally
        {
            if (task.IsCompleted) _inFlight = null;
        }
    }

    private Stored? Load()
    {
        var root = _pending?.Root ?? Read();
        if (root?["tokens"] is not JsonObject tokens || tokens["access_token"]?.GetValue<string>() is not { Length: > 0 } access)
            return null;

        var refresh = tokens["refresh_token"]?.GetValue<string>();
        if (_failedFor is not null && refresh != _failedFor) ResetBackoff();
        return new Stored(root, tokens, access, refresh);
    }

    private bool NeedsRefresh(JsonNode root, string access)
    {
        var now = _clock.GetUtcNow();
        if (_retryAfter is { } retryAfter && now < retryAfter) return false;
        if (_refreshNext) return true;
        if (DateTimeOffset.TryParse(root["last_refresh"]?.GetValue<string>(), out var last) && now - last > MaxAge) return true;
        return JwtExpiry(access) is { } expiry && expiry - now < ExpiryMargin;
    }

    private async Task RefreshAsync(JsonNode root, JsonObject tokens, string refresh, string fileRefresh)
    {
        using var timeout = new CancellationTokenSource(RefreshTimeout, _clock);
        try
        {
            var clientId = tokens["oauth_provenance"]?["client_id"]?.GetValue<string>() ?? DefaultClientId;
            using var response = await _http.PostAsJsonAsync(RefreshUrl, new
            {
                client_id = clientId,
                grant_type = "refresh_token",
                refresh_token = refresh,
                scope = "openid profile email",
            }, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Refreshing the Codex login was refused with {Status}", (int)response.StatusCode);
                BackOff(refresh);
                return;
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var access = body?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(access))
            {
                BackOff(refresh);
                return;
            }

            tokens["access_token"] = access;
            if (body?["refresh_token"]?.GetValue<string>() is { Length: > 0 } newRefresh) tokens["refresh_token"] = newRefresh;
            if (body?["id_token"]?.GetValue<string>() is { Length: > 0 } idToken) tokens["id_token"] = idToken;
            root["last_refresh"] = _clock.GetUtcNow().UtcDateTime.ToString("O");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Refreshing the Codex login failed: {Reason}", ex.Message);
            BackOff(refresh);
            return;
        }

        _refreshNext = false;
        ResetBackoff();
        _pending = TryWrite(root, fileRefresh) == WriteOutcome.Failed ? new Pending(root, fileRefresh) : null;
    }

    private void FlushPending()
    {
        if (_pending is not { } pending) return;
        if (TryWrite(pending.Root, pending.FileRefresh) != WriteOutcome.Failed) _pending = null;
    }

    private WriteOutcome TryWrite(JsonNode root, string fileRefresh)
    {
        lock (CodexAuthFileLock.Gate)
        {
            string? onDisk;
            try
            {
                onDisk = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path))?["tokens"]?["refresh_token"]?.GetValue<string>() : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Reading the Codex login before writing the refreshed one failed, keeping it in memory: {Reason}", ex.Message);
                return WriteOutcome.Failed;
            }

            if (onDisk != fileRefresh)
            {
                _logger.LogInformation("The Codex login was replaced by a new sign-in; the refreshed tokens are dropped");
                ResetBackoff();
                _refreshNext = false;
                return WriteOutcome.Superseded;
            }

            try
            {
                WriteAtomically(root);
                return WriteOutcome.Written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Writing the refreshed Codex login failed, keeping it in memory: {Reason}", ex.Message);
                return WriteOutcome.Failed;
            }
        }
    }

    private void BackOff(string refresh)
    {
        _delay = _delay == TimeSpan.Zero ? FirstBackoff : TimeSpan.FromTicks(Math.Min(_delay.Ticks * 2, MaxBackoff.Ticks));
        _retryAfter = _clock.GetUtcNow() + _delay;
        _failedFor = refresh;
    }

    private void ResetBackoff()
    {
        _delay = TimeSpan.Zero;
        _retryAfter = null;
        _failedFor = null;
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
