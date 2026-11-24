using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations.ModelGateway;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// Runs Google's Antigravity CLI (<c>agy</c>) headless: <c>agy -p &lt;prompt&gt; --output-format stream-json</c>.
/// With a Gemini API key saved on the provider the agent never holds it: the CLI is pointed at the
/// model gateway (<c>AGY_LLM_GATEWAY_URL</c>) with the run's placeholder as its key, and the gateway adds the real
/// one. Without a key it uses the Google sign-in made in the provider terminal, a copy of which the run is given
/// (the sign-in cannot go through the gateway, which speaks the Gemini API only). <see cref="AiProvider.BaseUrl"/> is ignored. <see cref="AiProvider.Model"/> is passed as <c>--model</c>
/// when set; blank leaves the CLI's own default.
///
/// A conversation is resumed with <c>--conversation &lt;id&gt;</c>. Its id is the <c>conversation_id</c> the stream
/// names, and the conversation is one SQLite file under <c>~/.gemini/antigravity-cli/conversations</c>, which is
/// saved after a turn and written back before a resumed one.
///
/// The ILD MCP server and the provider's custom MCP servers reach the CLI through the user-level
/// <c>~/.gemini/config/mcp_config.json</c> of the home the run's CLIs have, the one place it reads them that is
/// not the repository: the file is written before the launch and put back as it was after. Where that home is
/// shared (ILD running agents on its own machine), two runs at once would share the file too.
/// </summary>
public sealed class AgyAdapter : CliAgentAdapterBase
{
    private readonly ILogger _logger;

    public AgyAdapter(ILogger<AgyAdapter>? logger = null, IProcessEnvironment? environment = null)
        : base(environment)
    {
        _logger = logger ?? NullLogger<AgyAdapter>.Instance;
    }

    public AgyAdapter(IServiceScopeFactory scopeFactory, ILogger<AgyAdapter>? logger = null, IProcessEnvironment? environment = null)
        : base(scopeFactory, environment)
    {
        _logger = logger ?? NullLogger<AgyAdapter>.Instance;
    }

    public override string Name => "Agy";
    public override string[] SupportedProviderTypes => ["agy"];

    public override ConfigFieldDescriptor[] ConfigSchema => [CustomMcpServersField, ExtraArgsField];
    public override AdapterModelSupport ModelSupport => AdapterModelSupport.Optional;
    public override AgentAdapterTraits Traits => TypeTraits;

    public override CredentialPath? CredentialPathFor(AiProvider provider)
        => ModelCredentialSource.UpstreamKey(provider) is null ? CredentialPath.ReadOnlyCopy : CredentialPath.Gateway;

    /// <summary>The files, relative to the home, that the CLI keeps its Google sign-in in once it stores tokens in a file.</summary>
    internal static readonly string[] LoginFiles =
    [
        ".gemini/antigravity-cli/jetski-standalone-oauth-token",
        ".gemini/antigravity-cli/antigravity-oauth-token",
    ];

    /// <summary>Set, the CLI keeps its sign-in in a file instead of the OS keyring, which a run's home and the terminal's have none of.</summary>
    public const string ForceFileTokenStorage = "AGY_CLI_FORCE_FILE_TOKEN_STORAGE";

    /// <summary>
    /// Points the CLI at the gateway's Gemini route. The lease's token is sent as the bearer key, which the
    /// gateway swaps for the provider's real key; it speaks the Gemini wire protocol the CLI defaults to.
    /// </summary>
    public static void ApplyGateway(IDictionary<string, string?> environment, ModelGatewayLease gateway)
    {
        environment["AGY_LLM_GATEWAY_URL"] = gateway.UrlFor(GatewayRoutes.Google);
        environment["AGY_LLM_GATEWAY_API_KEY"] = gateway.Token;
    }

    protected override string CliName => "agy";
    protected override string StartHelp
        => "install or update the Antigravity CLI from the AI Provider page, or make sure the agy binary is on PATH.";
    protected override ILogger Logger => _logger;

    protected override CliAgentTurn CreateTurn(AgentExecutionContext ctx, string worktreePath, Lazy<ModelGatewayLease?> gateway)
        => new Turn(this, ctx, worktreePath, gateway);

    private sealed class Turn(AgyAdapter adapter, AgentExecutionContext ctx, string worktreePath, Lazy<ModelGatewayLease?> gateway) : CliAgentTurn
    {
        private readonly string _home = HomeFor(adapter, worktreePath);
        private readonly bool _hasKey = ModelCredentialSource.UpstreamKey(ctx.Provider) is not null;
        private bool _hasLogin;
        private string? _mcpConfigPath;
        private string? _mcpConfigBefore;
        private StreamResult? _stdout;

        public override string BinaryPath { get; } = AiProviderConfig.Parse(ctx.Provider.Config)
            .BinaryPathOr(ManagedAgentInstall.ResolveCommand(ManagedAgentCatalog.Agy, adapter.EnvironmentVariables));

        public override bool IsSessionId(string sessionId) => Guid.TryParseExact(sessionId, "D", out _);

        public override async Task PrepareAsync()
        {
            if (!_hasKey) _hasLogin = await adapter.CopyStoredLoginAsync(_home, ctx.Cancel);

            var servers = BuildMcpServers(ctx.Provider, ctx.RunContext, ctx.ToolAllowlist, ctx.ChatSessionId, adapter.RunFacingEnvironmentFor(ctx), ctx.NoTools);
            if (servers.Count == 0) return;

            var path = McpConfigPath(_home);
            var existing = await adapter.ParseSessionFileAsync(path, reader => reader.ReadToEnd(), ctx.Cancel);
            var merged = MergeMcpConfig(existing, servers);
            await adapter.RunHost.CreateDirectoryAsync(Path.GetDirectoryName(path)!, ctx.Cancel);
            await adapter.RunHost.WriteFileAsync(path, merged, ctx.Cancel);
            _mcpConfigPath = path;
            _mcpConfigBefore = existing;
        }

        // Reuses the conversation file agy already has, else writes the stored one back; false when there is neither.
        public override async Task<bool> RestoreSessionAsync(string sessionId)
        {
            var path = ConversationPath(_home, sessionId);
            return await adapter.RunHost.FileExistsAsync(path, ctx.Cancel)
                || await adapter.RestoreSessionFileAsync(ctx, sessionId, path, SessionSnapshotFormats.AgyDb);
        }

        public override async Task<string?> SaveSessionAsync(string sessionId)
        {
            await adapter.PersistSessionFileAsync(ctx, sessionId, ConversationPath(_home, sessionId), SessionSnapshotFormats.AgyDb, adapter._logger);
            return null;
        }

        public override Task<AgentLaunch> LaunchAsync()
        {
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (_hasKey)
            {
                if (gateway.Value is { } lease) ApplyGateway(environment, lease);
            }
            else if (_hasLogin)
                environment[ForceFileTokenStorage] = "1";
            else if (gateway.Value is not null)
                throw Failed("[agy-error] this provider has no Gemini API key and no Google sign-in: save a key on the AI Provider page, or use Open terminal to sign in.");

            var psi = BuildRunProcessStartInfo(
                BinaryPath, worktreePath, ctx.Prompt, ResumeSessionId, ctx.AdditionalAllowedDirectories, ctx.Provider.Model,
                ExtraCliArgs.ForLaunch(ctx.Provider, adapter._logger));
            return Task.FromResult(new AgentLaunch(psi, environment));
        }

        public override async Task ReadStdoutAsync(StreamReader stdout)
            => _stdout = await ReadStreamJsonAsync(stdout, ctx.ProgressCallback, ctx.OnSessionId, ctx.Cancel);

        public override StreamResult ParseStream() => _stdout!;

        public override BlankOutput BlankOutput => BlankOutput.Failure;

        // The CLI retries a failed call for minutes and logs each attempt to stderr; the error its result names is the one that matters.
        public override bool ProviderErrorIsTheFailure => true;

        public override Task<TokenUsage?> UsageAsync() => Task.FromResult(AdapterUsageParser.ParseAgy(_stdout!.RawStdout));

        public override async ValueTask DisposeAsync()
        {
            if (_mcpConfigPath is null) return;
            try
            {
                if (_mcpConfigBefore is null)
                    await adapter.RunHost.DeleteAsync([_mcpConfigPath]);
                else
                    await adapter.RunHost.WriteFileAsync(_mcpConfigPath, _mcpConfigBefore);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                adapter._logger.LogWarning(ex, "Could not put back the Antigravity MCP config {Path}", _mcpConfigPath);
            }
        }
    }

    /// <summary>The flags <see cref="BuildRunProcessStartInfo"/> sets, which a provider's extra CLI arguments may not repeat.</summary>
    internal static readonly string[] ReservedCliFlags =
        ["-p", "--print", "--prompt", "--dangerously-skip-permissions", "--output-format", "--input-format", "--conversation", "--resume", "-r",
         "--continue", "-c", "--add-dir", "--disable-slash-commands", "--settings", "--sandbox", "--mode", "--remote-agent", "--remote-control"];

    /// <summary>
    /// Antigravity runs on a Gemini API key kept on the provider or on the Google sign-in of its terminal, and its only tool is <c>ild</c>: its own
    /// tools cannot be turned off, so it takes no tool-less call, and an explicit selection without <c>ild</c> is
    /// how ILD is turned off. Declared after <see cref="ReservedCliFlags"/>, which it reads.
    /// </summary>
    internal static readonly AgentAdapterTraits TypeTraits = new()
    {
        DisplayName = "Antigravity",
        CliAuth = new("Antigravity", "sign in with your Google account (or leave it and save a Gemini API key on the provider)"),
        SupportedTools = AiToolCatalog.IldOnlyTools,
        EmptyToolSelectionMeansNone = true,
        SupportsNoTools = false,
        SupportsSessionResume = true,
        ExtraCliArgs = ExtraCliArgsRules.None with { ReservedFlags = ReservedCliFlags },
    };

    public static ProcessStartInfo BuildRunProcessStartInfo(
        string binaryPath,
        string worktreePath,
        string prompt,
        string? sessionId,
        IReadOnlyList<string>? additionalAllowedDirectories = null,
        string? model = null,
        IReadOnlyList<string>? extraArgs = null)
    {
        var psi = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = worktreePath,
        };

        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--disable-slash-commands");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");

        foreach (var dir in additionalAllowedDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(dir) || string.Equals(dir, worktreePath, StringComparison.Ordinal)) continue;
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(dir);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model);
        }

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            psi.ArgumentList.Add("--conversation");
            psi.ArgumentList.Add(sessionId);
        }

        foreach (var arg in extraArgs ?? [])
            psi.ArgumentList.Add(arg);

        // Keep the prompt last so its text is unambiguously the option's value.
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        return psi;
    }

    /// <summary>
    /// Gives a run in a home of its own a copy of the Google sign-in the provider terminal stored, which only ILD can read.
    /// A run on this machine's own home already has it. False when there is no stored sign-in.
    /// </summary>
    private async Task<bool> CopyStoredLoginAsync(string runHome, CancellationToken ct)
    {
        var storeHome = EnvironmentVariables.Get("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var found = false;
        foreach (var relative in LoginFiles)
        {
            string text;
            try
            {
                var path = Path.Combine(storeHome, relative);
                var info = new FileInfo(path);
                if (!info.Exists || info.LinkTarget is not null || info.Length is 0 or > CredentialImport.MaxBytes) continue;
                text = await File.ReadAllTextAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read the stored Antigravity sign-in {File}", relative);
                continue;
            }

            found = true;
            if (!string.Equals(runHome, storeHome, StringComparison.Ordinal))
            {
                var target = Path.Combine(runHome, relative);
                await RunHost.CreateDirectoryAsync(Path.GetDirectoryName(target)!, ct);
                await RunHost.WriteFileAsync(target, text, ct);
            }
        }
        return found;
    }

    private static string HomeFor(AgyAdapter adapter, string worktreePath)
        => adapter.RunHost.RunHomeFor(worktreePath)
            ?? (adapter.EnvironmentVariables.Get("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string ConversationPath(string home, string conversationId)
        => Path.Combine(home, ".gemini", "antigravity-cli", "conversations", $"{conversationId}.db");

    internal static string McpConfigPath(string home) => Path.Combine(home, ".gemini", "config", "mcp_config.json");

    /// <summary>
    /// The MCP servers a launch is given, by name, in the shape the CLI's <c>mcp_config.json</c> reads: the built-in
    /// <c>ild</c> server (only when that tool is selected) and the provider's custom servers, which are given even
    /// when <c>ild</c> is off. Empty when there is nothing to write.
    /// </summary>
    internal static Dictionary<string, object?> BuildMcpServers(
        AiProvider provider, LoopRunContext runContext, IReadOnlyList<string>? allowlist, Guid? chatSessionId, IProcessEnvironment? environment, bool noTools)
    {
        var selected = SelectMcpServers(TypeTraits, provider, allowlist, noTools, environment);
        var servers = new Dictionary<string, object?>();
        if (selected.Ild && ClaudeCodeAdapter.BuildIldMcpEntry(runContext, chatSessionId, environment) is { } ild)
            servers["ild"] = ild;
        foreach (var server in selected.Custom)
            servers[server.Name] = CustomMcpServers.BuildCustomMcpEntry(server);
        return servers;
    }

    /// <summary>
    /// <paramref name="existing"/> (the file as it is, null when there is none) with <paramref name="servers"/> set over
    /// what it names, so a server the user keeps there stays. A file that is not a JSON object is replaced.
    /// </summary>
    internal static string MergeMcpConfig(string? existing, IReadOnlyDictionary<string, object?> servers)
    {
        JsonObject root;
        try { root = JsonNode.Parse(existing ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { root = new JsonObject(); }

        var named = root["mcpServers"] as JsonObject ?? new JsonObject();
        foreach (var (name, entry) in servers)
            named[name] = JsonSerializer.SerializeToNode(entry);
        root["mcpServers"] = named;
        return root.ToJsonString();
    }

    /// <summary>
    /// Reads the <c>stream-json</c> events. The <c>result</c> event holds the answer, the conversation id and, on a
    /// failed turn, the provider's error; a stream that ends without one ended before its turn did.
    /// </summary>
    internal static async Task<StreamResult> ReadStreamJsonAsync(
        StreamReader reader, Func<string, Task>? progressCallback, Action<string>? onSessionId, CancellationToken ct)
    {
        var raw = new StringBuilder();
        var deltas = new StringBuilder();
        string? sessionId = null;
        string? response = null;
        string? error = null;
        var sawEvents = false;
        var sawResult = false;

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            raw.AppendLine(line);
            if (!TryParseObject(line, out var doc)) continue;

            using (doc)
            {
                sawEvents = true;
                var root = doc!.RootElement;
                switch (GetString(root, "event"))
                {
                    case "init":
                        if (GetString(root, "conversation_id") is { Length: > 0 } initial && sessionId is null)
                        {
                            sessionId = initial;
                            FireSessionId(onSessionId, initial);
                        }
                        break;

                    case "step_update" when root.TryGetProperty("step_update", out var step)
                        && GetString(step, "step_type") == "agent_response" && GetString(step, "text_delta") is { Length: > 0 } text:
                        deltas.Append(text);
                        if (progressCallback is not null) await progressCallback(text.TrimEnd()).ConfigureAwait(false);
                        break;

                    case "result" when root.TryGetProperty("result", out var result):
                        sawResult = true;
                        sessionId = GetString(result, "conversation_id") is { Length: > 0 } finalId ? finalId : sessionId;
                        response = GetString(result, "response");
                        var status = GetString(result, "status");
                        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
                            error = GetString(result, "error") is { Length: > 0 } message ? message : $"the turn ended with status {status ?? "unknown"}";
                        break;
                }
            }
        }

        var answer = (string.IsNullOrWhiteSpace(response) ? deltas.ToString() : response).Trim();
        return new StreamResult(
            answer, sessionId, error, sawEvents, raw.ToString(),
            sawEvents && !sawResult ? "[agy-error] the stream ended before the turn did" : null);
    }

    private static bool TryParseObject(string line, out JsonDocument? doc)
    {
        doc = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var parsed = JsonDocument.Parse(line);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                doc = parsed;
                return true;
            }
            parsed.Dispose();
        }
        catch (JsonException) { }
        return false;
    }
}
