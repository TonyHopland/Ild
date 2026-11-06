using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations.ModelGateway;

/// <summary>
/// After a provider terminal closes, takes the logins it made out of the throwaway home the CLI ran in
/// and into the credential store (ADR-0031). The terminal runs as the agent uid, so what it left is
/// the agent's to write: only a fixed list of files is read, each as the agent and never through a link,
/// and a file is kept only when it parses and really holds a login, so a CLI that wrote nothing worth
/// keeping, or a planted file, cannot replace a working login.
/// </summary>
public static class CredentialImport
{
    private const int MaxBytes = 256 * 1024;

    private sealed record Login(string RelativePath, Func<JsonNode, bool> HoldsLogin, bool Jsonc = false);

    private static readonly Login[] Logins =
    [
        new(".claude/.credentials.json", node => node["claudeAiOauth"]?["accessToken"] is not null),
        new(".codex/auth.json", node => node["tokens"]?["access_token"] is not null || node["OPENAI_API_KEY"] is not null),
        new(".copilot/config.json", node => node["authTokens"] is JsonObject { Count: > 0 }, Jsonc: true),
        new(".pi/agent/auth.json", node => node is JsonObject { Count: > 0 }),
        new(".local/share/opencode/auth.json", node => node is JsonObject { Count: > 0 }),
    ];

    /// <returns>The files imported, relative to the home.</returns>
    public static async Task<IReadOnlyList<string>> ImportAsync(string throwawayHome, string storeHome, ILogger logger, CancellationToken ct)
    {
        var imported = new List<string>();
        foreach (var login in Logins)
        {
            try
            {
                var source = Path.Combine(throwawayHome, login.RelativePath);
                if (await AgentWritableFiles.GetFileLengthAsync(source, ct) is not ({ } length and > 0 and <= MaxBytes)) continue;

                string? text = null;
                await AgentWritableFiles.ReadFileRangeAsync(source, 0, length, async content =>
                {
                    using var reader = new StreamReader(content);
                    text = await reader.ReadToEndAsync(ct);
                });
                if (text is null || !Holds(login, text)) continue;

                Write(Path.Combine(storeHome, login.RelativePath), text);
                imported.Add(login.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                logger.LogWarning(ex, "Could not import the login {File} from the provider terminal: {Reason}", login.RelativePath, ex.Message);
            }
        }
        return imported;
    }

    private static bool Holds(Login login, string text)
    {
        if (login.Jsonc)
            text = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        try { return JsonNode.Parse(text) is { } node && login.HoldsLogin(node); }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
                stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
