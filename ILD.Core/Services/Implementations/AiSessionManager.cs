using System.Text.Json;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// JSON-backed implementation of <see cref="IAiSessionManager"/>. The on-disk
/// shape is a JSON array of <see cref="RunSessionEntry"/> — kept as a flat
/// list (rather than an object map) so the existing DB rows continue to
/// parse without a migration.
/// </summary>
public sealed class AiSessionManager : IAiSessionManager
{
    // SessionsJson is parsed via raw JsonDocument below with camelCase
    // property names; the writer must match or the next resolve will silently
    // miss the entry and AI nodes will start a fresh session every time.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public string? Resolve(string? sessionsJson, Guid providerId)
    {
        if (string.IsNullOrEmpty(sessionsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(sessionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                // Tolerate both camelCase (current) and PascalCase (legacy
                // pre-fix payloads in the DB) so existing runs continue to
                // resolve their session after upgrade.
                var pid = TryGetStringInsensitive(entry, "providerId");
                if (pid == null || !Guid.TryParse(pid, out var parsedPid) || parsedPid != providerId)
                    continue;
                return TryGetStringInsensitive(entry, "sessionId");
            }
        }
        catch { /* malformed JSON: treat as no session */ }
        return null;
    }

    public async Task PersistAsync(LoopRun run, Guid providerId, string? sessionId, Func<LoopRun, Task> persist)
    {
        var sessions = ParseSessions(run.SessionsJson);
        if (sessionId is null)
        {
            sessions.RemoveAll(s => s.ProviderId == providerId.ToString());
        }
        else
        {
            var existing = sessions.Find(s => s.ProviderId == providerId.ToString());
            if (existing is not null)
                existing.SessionId = sessionId;
            else
                sessions.Add(new RunSessionEntry(providerId.ToString(), sessionId));
        }
        run.SessionsJson = JsonSerializer.Serialize(sessions, JsonOptions);
        await persist(run);
    }

    private static List<RunSessionEntry> ParseSessions(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new List<RunSessionEntry>();
        try { return JsonSerializer.Deserialize<List<RunSessionEntry>>(json, JsonOptions) ?? new List<RunSessionEntry>(); }
        catch { return new List<RunSessionEntry>(); }
    }

    private static string? TryGetStringInsensitive(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        }
        return null;
    }

    private sealed class RunSessionEntry
    {
        public string ProviderId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public RunSessionEntry(string providerId, string sessionId)
        {
            ProviderId = providerId;
            SessionId = sessionId;
        }
    }
}
