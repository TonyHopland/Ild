using System.Text.Json;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// One custom MCP server attached to an AI provider through the per-provider
/// "Custom MCP servers (JSON)" config field. This is the normalized, internal
/// superset of the two CLI-native shapes: <see cref="Command"/> holds the
/// executable plus any inline argv tokens, <see cref="Args"/> is appended after
/// it, and <see cref="Env"/> becomes the server process's environment. Each
/// adapter formats this into its own native config shape (see
/// <see cref="OpenCodeAdapter"/> and <see cref="ClaudeCodeAdapter"/>).
/// <see cref="Command"/> is guaranteed non-empty — <see cref="CustomMcpServers.Parse"/>
/// drops any server whose command is missing.
/// </summary>
public sealed record CustomMcpServer(
    string Name,
    IReadOnlyList<string> Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env);

/// <summary>
/// Tolerant parser for the per-provider "Custom MCP servers (JSON)" blob.
/// Mirrors <see cref="AiProviderConfig.Parse"/>'s fail-open contract: malformed
/// or partially-malformed input never throws — it yields whatever well-formed
/// servers it can and silently skips the rest, so an AI node run is never taken
/// down by a bad config value.
///
/// Accepted shape is a JSON object mapping a server name to its definition:
/// <code>
/// {
///   "chrome-devtools": {
///     "command": ["npx", "-y", "chrome-devtools-mcp@latest", "--headless"],
///     "env": { "FOO": "bar" }
///   }
/// }
/// </code>
/// <c>command</c> may be a string or an array of strings; <c>args</c> and
/// <c>env</c> are optional.
/// </summary>
public static class CustomMcpServers
{
    /// <summary>
    /// The server name ILD reserves for its own MCP entry. A custom server with
    /// this name is skipped so it can never clobber the injected <c>ild</c> server.
    /// </summary>
    private const string ReservedIldName = "ild";

    /// <summary>Parse the raw custom-MCP JSON into normalized servers. Never throws.</summary>
    public static IReadOnlyList<CustomMcpServer> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<CustomMcpServer>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch
        {
            return Array.Empty<CustomMcpServer>();
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Array.Empty<CustomMcpServer>();

            var servers = new List<CustomMcpServer>();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var name = property.Name;
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(name, ReservedIldName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var server = ParseServer(name, property.Value);
                if (server != null)
                    servers.Add(server);
            }

            return servers;
        }
    }

    private static CustomMcpServer? ParseServer(string name, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;

        var command = ReadStringList(value, "command");
        // A server with no command has nothing to launch — skip it rather than
        // emit a broken entry into the agent's config.
        if (command.Count == 0) return null;

        var args = ReadStringList(value, "args");
        var env = ReadEnv(value);

        return new CustomMcpServer(name, command, args, env);
    }

    /// <summary>Read a property that may be a single string or an array of strings.</summary>
    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var value))
            return Array.Empty<string>();

        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return string.IsNullOrEmpty(single) ? Array.Empty<string>() : new[] { single };
        }

        if (value.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrEmpty(s)) items.Add(s);
        }
        return items;
    }

    private static IReadOnlyDictionary<string, string> ReadEnv(JsonElement obj)
    {
        if (!obj.TryGetProperty("env", out var env) || env.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>();
        foreach (var property in env.EnumerateObject())
        {
            // Coerce scalar values to their raw text so numeric/boolean env
            // values survive; skip objects/arrays which have no shell meaning.
            var v = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                _ => null,
            };
            if (v != null) result[property.Name] = v;
        }
        return result;
    }
}
