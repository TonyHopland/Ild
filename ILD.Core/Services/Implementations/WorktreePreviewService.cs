using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public sealed class WorktreePreviewService : IWorktreePreviewService, IDisposable
{
    private const string ConfigFileName = "ild.config.json";
    private static readonly Regex TemplateTokenRegex = new("\\$\\{([^}]+)\\}", RegexOptions.Compiled);

    /// <summary>
    /// A preview hostname's leading label: <c>wi-{workItemId}</c>, optionally
    /// followed by <c>-{serviceName}</c>. Anchoring the id to digits is what keeps
    /// the split unambiguous for service names that themselves contain hyphens
    /// (<c>wi-12-work-item-server</c> is item 12's <c>work-item-server</c>, not
    /// item <c>12-work</c>'s <c>item-server</c>).
    /// </summary>
    private static readonly Regex PreviewHostLabelRegex =
        new(@"^wi-(?<id>[0-9]+)(?:-(?<service>.+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, PreviewRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly PreviewProxyBase _proxyBase;
    private readonly ILogger<WorktreePreviewService> _logger;
    private readonly string? _agentUser;
    private readonly string? _agentGroup;
    private readonly string? _agentHome;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public WorktreePreviewService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        PreviewProxyBase proxyBase,
        ILogger<WorktreePreviewService> logger)
        : this(httpClientFactory, configuration, proxyBase, logger,
            AgentIsolation.AgentUser, AgentIsolation.AgentGroup, AgentIsolation.AgentHome)
    {
    }

    /// <summary>
    /// The uid-isolation parameters taken explicitly rather than read from the
    /// process environment — the convention <c>ManagedAgentService</c> and the
    /// <c>AgentIsolation</c> overloads already use, so a test can drive the
    /// isolated shape without setting variables that would turn isolation on for
    /// every other test in the host process. The DI constructor above supplies the
    /// production values; all three are null when isolation is off, and every
    /// decision keyed off them then reduces to the pre-isolation behaviour.
    /// </summary>
    public WorktreePreviewService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        PreviewProxyBase proxyBase,
        ILogger<WorktreePreviewService> logger,
        string? agentUser,
        string? agentGroup,
        string? agentHome)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _proxyBase = proxyBase;
        _logger = logger;
        // Blank and unset are the same thing throughout AgentIsolation; normalise
        // once here so every decision below can just test for null.
        _agentUser = NonEmpty(agentUser);
        _agentGroup = NonEmpty(agentGroup);
        _agentHome = NonEmpty(agentHome);
    }

    /// <summary>
    /// The <c>HOME</c> a preview step actually runs with once it has crossed to the
    /// agent uid, or <c>null</c> when the crossing leaves <c>HOME</c> alone — which
    /// covers both "isolation is off" and "isolation is on but no agent home is
    /// configured". <see cref="AgentIsolation.ResolveChildHome"/> owns the rule and
    /// the crossing applies the same answer, so the npm prefix derived from it here
    /// cannot drift from the <c>HOME</c> the child is really given.
    /// </summary>
    private string? ChildHome => AgentIsolation.ResolveChildHome(_agentUser, _agentHome);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async Task<WorktreePreviewResponse> GetStatusAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWorktreePath(worktreePath);
        var loaded = await LoadConfigAsync(normalized, cancellationToken);
        if (!loaded.Configured)
        {
            return new WorktreePreviewResponse
            {
                Configured = false,
                State = "notConfigured",
                WorktreePath = normalized,
                Message = loaded.Message,
            };
        }

        if (_runtimes.TryGetValue(normalized, out var runtime))
        {
            return BuildResponse(loaded, runtime);
        }

        return BuildStoppedResponse(loaded);
    }

    public async Task<WorktreePreviewResponse> StartAsync(string worktreePath, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWorktreePath(worktreePath);
        options ??= new WorktreePreviewStartOptions();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await LoadConfigAsync(normalized, cancellationToken);
            if (!loaded.Configured || loaded.Config == null)
            {
                throw new InvalidOperationException(loaded.Message ?? "No ild.config.json preview profile found.");
            }

            if (_runtimes.TryGetValue(normalized, out var existing))
            {
                var existingResponse = BuildResponse(loaded, existing);
                if (existingResponse.State == "running")
                {
                    return existingResponse;
                }

                await StopRuntimeAsync(existing, cancellationToken);
                _runtimes.TryRemove(normalized, out _);
            }

            var profileName = SelectProfileName(loaded.Config, options.ProfileName);
            if (!loaded.Config.Preview!.Profiles.TryGetValue(profileName, out var profile) || profile == null)
            {
                throw new InvalidOperationException($"Preview profile '{profileName}' not found.");
            }

            ValidateProfile(profileName, profile);

            var runtime = await CreateRuntimeAsync(normalized, loaded, profileName, profile, options, cancellationToken);

            foreach (var service in profile.Services)
            {
                runtime.AddProcess(await LaunchServiceProcessAsync(service, runtime, cancellationToken));
            }

            foreach (var service in profile.Services)
            {
                var healthUrl = ResolveHealthUrl(service, runtime);
                await WaitForHealthAsync(service.Name, healthUrl, cancellationToken);
            }

            _runtimes[normalized] = runtime;
            return BuildResponse(loaded, runtime);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorktreePreviewResponse> StopAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWorktreePath(worktreePath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await LoadConfigAsync(normalized, cancellationToken);
            if (_runtimes.TryRemove(normalized, out var runtime))
            {
                await StopRuntimeAsync(runtime, cancellationToken);
                return BuildStoppedResponse(loaded, runtime.ProfileName, runtime.PublicHost, runtime.StateDirectory, loaded.ConfigPath);
            }

            return BuildStoppedResponse(loaded);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorktreePreviewResponse> StartServiceAsync(string worktreePath, string serviceName, WorktreePreviewStartOptions? options = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWorktreePath(worktreePath);
        options ??= new WorktreePreviewStartOptions();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await LoadConfigAsync(normalized, cancellationToken);
            if (!loaded.Configured || loaded.Config == null)
            {
                throw new InvalidOperationException(loaded.Message ?? "No ild.config.json preview profile found.");
            }

            var profileName = SelectProfileName(loaded.Config, options.ProfileName);
            if (!loaded.Config.Preview!.Profiles.TryGetValue(profileName, out var profile) || profile == null)
            {
                throw new InvalidOperationException($"Preview profile '{profileName}' not found.");
            }

            ValidateProfile(profileName, profile);

            var service = profile.Services.FirstOrDefault(s => string.Equals(s.Name, serviceName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Preview service '{serviceName}' not found in profile '{profileName}'.");

            if (_runtimes.TryGetValue(normalized, out var runtime))
            {
                var existing = runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, service.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!existing.Process.HasExited)
                        return BuildResponse(loaded, runtime);

                    // An exited process lingers in the runtime so its log/exit code
                    // stays visible; restarting the service replaces it cleanly.
                    await StopProcessAsync(existing, cancellationToken);
                    runtime.RemoveProcess(existing);
                }

                EnsureServicePortAllocated(service, runtime, options.PortOverrides);
            }
            else
            {
                runtime = await CreateRuntimeAsync(normalized, loaded, profileName, profile, options, cancellationToken);
                _runtimes[normalized] = runtime;
            }

            runtime.AddProcess(await LaunchServiceProcessAsync(service, runtime, cancellationToken));
            await WaitForHealthAsync(service.Name, ResolveHealthUrl(service, runtime), cancellationToken);

            return BuildResponse(loaded, runtime);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorktreePreviewResponse> StopServiceAsync(string worktreePath, string serviceName, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWorktreePath(worktreePath);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await LoadConfigAsync(normalized, cancellationToken);
            if (!_runtimes.TryGetValue(normalized, out var runtime))
                return BuildStoppedResponse(loaded);

            var process = runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, serviceName, StringComparison.OrdinalIgnoreCase));
            if (process != null)
            {
                await StopProcessAsync(process, cancellationToken);
                runtime.RemoveProcess(process);
            }

            if (runtime.Processes.Count == 0)
            {
                _runtimes.TryRemove(normalized, out _);
                return BuildStoppedResponse(loaded, runtime.ProfileName, runtime.PublicHost, runtime.StateDirectory, loaded.ConfigPath);
            }

            return BuildResponse(loaded, runtime);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetServiceConfigAsync(string worktreePath, string serviceName, string? profileName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return null;

        var normalized = NormalizeWorktreePath(worktreePath);
        var loaded = await LoadConfigAsync(normalized, cancellationToken);
        if (!loaded.Configured || loaded.Config == null)
            return null;

        var resolvedProfileName = SelectProfileName(loaded.Config, profileName);
        var node = await LoadConfigNodeAsync(loaded.ConfigPath!, cancellationToken);
        var serviceNode = FindServiceNode(node, resolvedProfileName, serviceName);
        return serviceNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task UpdateServiceConfigAsync(string worktreePath, string serviceName, string serviceConfigJson, string? profileName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            throw new InvalidOperationException("A service name is required.");

        var normalized = NormalizeWorktreePath(worktreePath);
        var loaded = await LoadConfigAsync(normalized, cancellationToken);
        if (!loaded.Configured || loaded.Config == null)
            throw new InvalidOperationException(loaded.Message ?? "No ild.config.json preview profile found.");

        var resolvedProfileName = SelectProfileName(loaded.Config, profileName);

        // Parse and validate the edited service through the same model and rules the
        // preview-start path uses, so a config that would fail to start is rejected
        // here rather than silently written to disk.
        PreviewServiceConfig edited;
        try
        {
            edited = JsonSerializer.Deserialize<PreviewServiceConfig>(serviceConfigJson, _jsonOptions)
                ?? throw new InvalidOperationException("Service config is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Service config is not valid JSON: {ex.Message}");
        }

        if (!string.Equals(edited.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Service config name '{edited.Name}' must match '{serviceName}'; this editor updates a service in place.");

        ValidateService(resolvedProfileName, edited);

        var node = await LoadConfigNodeAsync(loaded.ConfigPath!, cancellationToken);
        if (node?["preview"]?["profiles"]?[resolvedProfileName]?["services"] is not JsonArray services)
            throw new InvalidOperationException($"Preview profile '{resolvedProfileName}' not found.");

        var index = -1;
        for (var i = 0; i < services.Count; i++)
        {
            if (string.Equals(services[i]?["name"]?.GetValue<string>(), serviceName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new InvalidOperationException($"Preview service '{serviceName}' not found in profile '{resolvedProfileName}'.");

        services[index] = JsonNode.Parse(serviceConfigJson);
        await File.WriteAllTextAsync(loaded.ConfigPath!, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    public async Task<string?> GetServiceLogAsync(string worktreePath, string serviceName, int maxBytes = 64 * 1024, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return null;

        // The log file is named after the service inside the state directory, so a
        // name with a path separator (or "..") could escape it. StartServiceAsync
        // composes the path the same way; reject anything that isn't a bare file
        // name rather than reading an arbitrary file off disk.
        if (!string.Equals(serviceName, Path.GetFileName(serviceName), StringComparison.Ordinal))
            return null;

        var normalized = NormalizeWorktreePath(worktreePath);
        var logPath = Path.Combine(BuildLogDirectory(normalized), $"{serviceName}.log");
        if (!File.Exists(logPath))
            return null;

        await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (maxBytes > 0 && stream.Length > maxBytes)
            stream.Seek(-maxBytes, SeekOrigin.End);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, string? customEnv = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWorktreePath(worktreePath);
        var loaded = await LoadConfigAsync(normalized, cancellationToken);
        if (!loaded.Configured || loaded.Config == null)
        {
            // Best effort: most repositories ship no ild.config.json preview
            // profile, so there is nothing to install. Skip instead of failing
            // and let the caller surface the reason as a warning.
            return new WorktreeInstallResult(false, loaded.Message ?? "No ild.config.json preview profile found.");
        }

        var resolvedProfileName = SelectProfileName(loaded.Config, profileName);
        if (!loaded.Config.Preview!.Profiles.TryGetValue(resolvedProfileName, out var profile) || profile == null)
        {
            throw new InvalidOperationException($"Preview profile '{resolvedProfileName}' not found.");
        }

        var stateDirectory = BuildStateDirectory(normalized);
        Directory.CreateDirectory(stateDirectory);
        var logDirectory = BuildLogDirectory(normalized);

        // Install needs no ports or running services — build a port-less runtime so
        // the shared install runner resolves ${WORKTREE}/${STATE_DIR} the same way
        // the preview start path does.
        var runtime = new PreviewRuntime(
            normalized,
            loaded.ConfigPath!,
            resolvedProfileName,
            stateDirectory,
            logDirectory,
            _configuration["ILD_PREVIEW_PUBLIC_HOST"] ?? "127.0.0.1",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new List<ManagedPreviewProcess>(),
            DotEnvParser.Parse(customEnv));

        await RunInstallStepsAsync(profile.Install, runtime, cancellationToken);
        return new WorktreeInstallResult(true);
    }

    public async Task<WorktreePreviewValidationResult> ValidateConfigAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeWorktreePath(worktreePath);
        var loaded = await LoadConfigAsync(normalized, cancellationToken);
        if (!loaded.Configured || loaded.Config == null)
        {
            return new WorktreePreviewValidationResult(false, null, Array.Empty<string>(), loaded.Message);
        }

        var resolvedProfileName = SelectProfileName(loaded.Config, profileName);
        if (!loaded.Config.Preview!.Profiles.TryGetValue(resolvedProfileName, out var profile) || profile == null)
        {
            throw new InvalidOperationException($"Preview profile '{resolvedProfileName}' not found.");
        }

        // Same validation the preview-start path applies, but without allocating
        // ports or launching anything — a pure dry run over the parsed config.
        ValidateProfile(resolvedProfileName, profile);
        return new WorktreePreviewValidationResult(
            true,
            resolvedProfileName,
            profile.Services.Select(s => s.Name).ToList(),
            null);
    }

    public bool IsPreviewRunning(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            return false;

        var normalized = Path.GetFullPath(worktreePath);
        return _runtimes.ContainsKey(normalized);
    }

    public async Task<PreviewTarget> ResolvePreviewTargetAsync(string hostLabel, IWorkItemManager workItems, CancellationToken cancellationToken = default)
    {
        var match = PreviewHostLabelRegex.Match(hostLabel?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.NotAPreviewHost,
                $"'{hostLabel}' is not a worktree preview hostname. Previews are served from "
                + "wi-<workItemId> (the service marked \"public\": true) or wi-<workItemId>-<serviceName>.");
        }

        var workItemId = match.Groups["id"].Value;
        var serviceName = match.Groups["service"].Success ? match.Groups["service"].Value : null;

        var workItem = await workItems.GetWorkItemAsync(workItemId);
        if (workItem == null)
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.UnknownWorkItem,
                $"There is no work item {workItemId}.");
        }

        if (string.IsNullOrWhiteSpace(workItem.WorktreePath))
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.NoWorktree,
                $"Work item {workItemId} has no worktree yet, so there is nothing to preview. "
                + "A worktree is created when a run reaches its Start node.");
        }

        if (!_runtimes.TryGetValue(NormalizeWorktreePath(workItem.WorktreePath), out var runtime))
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.PreviewNotRunning,
                $"The worktree preview for work item {workItemId} is not running. "
                + "Start it from the work item's Preview tab.");
        }

        ManagedPreviewProcess? process;
        if (serviceName == null)
        {
            var publicProcesses = runtime.Processes.Where(p => p.Service.Public && p.IsRunning).ToList();
            if (publicProcesses.Count == 0)
            {
                return PreviewTarget.Failed(
                    PreviewTargetOutcome.ServiceNotRunning,
                    $"No service marked \"public\": true is running in work item {workItemId}'s preview. "
                    + $"Start it, or address one service directly as wi-{workItemId}-<serviceName>.");
            }

            // Picking the first would silently serve one of them under a hostname
            // that just as fairly describes the others.
            if (publicProcesses.Count > 1)
            {
                var names = string.Join(", ", publicProcesses.Select(p => $"wi-{workItemId}-{p.Service.Name}"));
                return PreviewTarget.Failed(
                    PreviewTargetOutcome.AmbiguousService,
                    $"Work item {workItemId}'s preview is running {publicProcesses.Count} services marked "
                    + $"\"public\": true, so this hostname does not name one. Use {names}.");
            }

            process = publicProcesses[0];
        }
        else
        {
            process = runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, serviceName, StringComparison.OrdinalIgnoreCase));
            if (process == null || !process.IsRunning)
            {
                return PreviewTarget.Failed(
                    PreviewTargetOutcome.ServiceNotRunning,
                    $"Preview service '{serviceName}' is not running in work item {workItemId}'s preview.");
            }
        }

        if (!runtime.Ports.TryGetValue(process.Service.Port, out var port) || port <= 0)
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.ServiceNotRunning,
                $"Preview service '{process.Service.Name}' has no port allocated for alias '{process.Service.Port}'.");
        }

        return PreviewTarget.Resolved(port, process.Service.Name, process.Service.RewriteHost);
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes.Values)
        {
            try
            {
                StopRuntimeAsync(runtime, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort on shutdown.
            }
        }

        _gate.Dispose();
    }

    private static string NormalizeWorktreePath(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            throw new InvalidOperationException("Preview requires a worktree path.");

        return Path.GetFullPath(worktreePath);
    }

    private async Task<LoadedPreviewConfig> LoadConfigAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(worktreePath, ConfigFileName);
        if (!File.Exists(configPath))
        {
            return new LoadedPreviewConfig(false, worktreePath, configPath, null, $"No {ConfigFileName} found in worktree root.");
        }

        await using var stream = File.OpenRead(configPath);
        var config = await JsonSerializer.DeserializeAsync<IldWorkspaceConfig>(stream, _jsonOptions, cancellationToken);
        if (config?.Preview?.Profiles == null || config.Preview.Profiles.Count == 0)
        {
            return new LoadedPreviewConfig(false, worktreePath, configPath, null, "ild.config.json does not define any preview profiles.");
        }

        return new LoadedPreviewConfig(true, worktreePath, configPath, config, null);
    }

    // Parses ild.config.json into a mutable DOM so a single service entry can be read
    // or replaced without re-serializing the strongly-typed model (which would drop
    // fields the model does not surface). Tolerates comments and trailing commas, the
    // same as the strongly-typed loader.
    private static async Task<JsonNode?> LoadConfigNodeAsync(string configPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(configPath);
        return await JsonNode.ParseAsync(
            stream,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true },
            cancellationToken);
    }

    private static JsonNode? FindServiceNode(JsonNode? root, string profileName, string serviceName)
    {
        if (root?["preview"]?["profiles"]?[profileName]?["services"] is not JsonArray services)
            return null;

        return services.FirstOrDefault(node =>
            string.Equals(node?["name"]?.GetValue<string>(), serviceName, StringComparison.OrdinalIgnoreCase));
    }

    private static string SelectProfileName(IldWorkspaceConfig config, string? requestedProfile)
    {
        if (!string.IsNullOrWhiteSpace(requestedProfile))
            return requestedProfile.Trim();

        if (!string.IsNullOrWhiteSpace(config.Preview?.DefaultProfile))
            return config.Preview.DefaultProfile.Trim();

        return config.Preview!.Profiles.Keys.First();
    }

    private static void ValidateProfile(string profileName, PreviewProfileConfig profile)
    {
        if (profile.Services.Count == 0)
            throw new InvalidOperationException($"Preview profile '{profileName}' does not define any services.");

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in profile.Services)
        {
            ValidateService(profileName, service);
            if (!seenNames.Add(service.Name))
                throw new InvalidOperationException($"Preview profile '{profileName}' defines duplicate service name '{service.Name}'.");
        }
    }

    /// <summary>
    /// Per-service validation shared by <see cref="ValidateProfile"/> and the config
    /// editor's <see cref="UpdateServiceConfigAsync"/> — every rule a service must
    /// satisfy to be started. Duplicate-name detection across a profile stays in
    /// <see cref="ValidateProfile"/> since it is inherently cross-service.
    /// </summary>
    private static void ValidateService(string profileName, PreviewServiceConfig service)
    {
        if (string.IsNullOrWhiteSpace(service.Name))
            throw new InvalidOperationException($"Preview profile '{profileName}' has a service with no name.");
        if (string.IsNullOrWhiteSpace(service.Command))
            throw new InvalidOperationException($"Preview service '{service.Name}' has no command.");
        if (string.IsNullOrWhiteSpace(service.Port))
            throw new InvalidOperationException($"Preview service '{service.Name}' has no port alias.");
        if (string.IsNullOrWhiteSpace(service.HealthUrl))
            throw new InvalidOperationException($"Preview service '{service.Name}' must define healthUrl.");
        if (service.SuggestedPort is <= 0)
            throw new InvalidOperationException($"Preview service '{service.Name}' has invalid suggestedPort '{service.SuggestedPort}'.");
    }

    private static Dictionary<string, int> AllocatePorts(PreviewProfileConfig profile, IReadOnlyDictionary<string, int>? overrides)
    {
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var reservedPorts = new HashSet<int>();

        if (overrides != null)
        {
            foreach (var (alias, port) in overrides)
            {
                if (port <= 0)
                    throw new InvalidOperationException($"Preview port override for alias '{alias}' must be greater than zero.");
                if (!reservedPorts.Add(port))
                    throw new InvalidOperationException($"Preview port override '{port}' is assigned more than once.");
            }
        }

        foreach (var service in profile.Services)
        {
            if (ports.ContainsKey(service.Port))
                continue;

            if (overrides != null && overrides.TryGetValue(service.Port, out var overriddenPort))
            {
                ports[service.Port] = overriddenPort;
                continue;
            }

            var suggested = service.SuggestedPort;
            var port = suggested is > 0 && !reservedPorts.Contains(suggested.Value) && IsPortAvailable(suggested.Value)
                ? suggested.Value
                : FindFreePort(reservedPorts);

            ports[service.Port] = port;
            reservedPorts.Add(port);
        }

        foreach (var (alias, port) in ports)
        {
            if (!IsPortAvailable(port))
                throw new InvalidOperationException($"Preview port '{port}' for alias '{alias}' is already in use.");
        }

        return ports;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FindFreePort(ISet<int>? reservedPorts = null)
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (reservedPorts == null || !reservedPorts.Contains(port))
                    return port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    /// <summary>
    /// Builds the shared runtime for a worktree: resolves the public host, creates the
    /// state directory, allocates every profile service's port up front (so per-service
    /// starts resolve cross-service <c>${PORT:&lt;alias&gt;}</c> references), and runs the
    /// install steps unless skipped. Does not launch any service or store the runtime —
    /// the caller owns process startup and dictionary insertion.
    /// </summary>
    private async Task<PreviewRuntime> CreateRuntimeAsync(
        string normalized,
        LoadedPreviewConfig loaded,
        string profileName,
        PreviewProfileConfig profile,
        WorktreePreviewStartOptions options,
        CancellationToken cancellationToken)
    {
        var publicHost = ResolvePublicHost(options.PublicHost);
        var stateDirectory = BuildStateDirectory(normalized);
        Directory.CreateDirectory(stateDirectory);
        var logDirectory = BuildLogDirectory(normalized);

        var ports = AllocatePorts(profile, options.PortOverrides);
        var runtime = new PreviewRuntime(
            normalized,
            loaded.ConfigPath!,
            profileName,
            stateDirectory,
            logDirectory,
            publicHost,
            ports,
            new List<ManagedPreviewProcess>(),
            DotEnvParser.Parse(options.CustomEnv),
            options.WorkItemId);

        if (!options.SkipInstall)
        {
            await RunInstallStepsAsync(profile.Install, runtime, cancellationToken);
        }

        return runtime;
    }

    private string ResolvePublicHost(string? requestedHost)
        => string.IsNullOrWhiteSpace(requestedHost)
            ? (_configuration["ILD_PREVIEW_PUBLIC_HOST"] ?? "127.0.0.1")
            : requestedHost.Trim();

    /// <summary>
    /// Allocates a port for a single service whose alias is not already reserved on a
    /// running runtime — the case where a service was added to the config after the
    /// runtime was created. Honours an explicit override, otherwise prefers the
    /// service's suggested port and falls back to a free one.
    /// </summary>
    private static void EnsureServicePortAllocated(PreviewServiceConfig service, PreviewRuntime runtime, IReadOnlyDictionary<string, int>? overrides)
    {
        if (runtime.Ports.ContainsKey(service.Port))
            return;

        var reserved = new HashSet<int>(runtime.Ports.Values);
        int port;
        if (overrides != null && overrides.TryGetValue(service.Port, out var overridden))
        {
            if (overridden <= 0)
                throw new InvalidOperationException($"Preview port override for alias '{service.Port}' must be greater than zero.");
            if (reserved.Contains(overridden) || !IsPortAvailable(overridden))
                throw new InvalidOperationException($"Preview port '{overridden}' for alias '{service.Port}' is already in use.");
            port = overridden;
        }
        else
        {
            var suggested = service.SuggestedPort;
            port = suggested is > 0 && !reserved.Contains(suggested.Value) && IsPortAvailable(suggested.Value)
                ? suggested.Value
                : FindFreePort(reserved);
        }

        runtime.Ports[service.Port] = port;
    }

    private async Task RunInstallStepsAsync(IReadOnlyList<PreviewCommandConfig> installSteps, PreviewRuntime runtime, CancellationToken cancellationToken)
    {
        if (installSteps.Count == 0)
            return;

        var installLogPath = Path.Combine(runtime.LogDirectory, "install.log");
        foreach (var step in installSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Command))
                continue;

            var resolved = BuildResolvedStep(step, runtime, null);
            var result = await RunCommandAsync(resolved, cancellationToken);

            var builder = new StringBuilder();
            builder.AppendLine($"> {resolved.Command}");
            if (!string.IsNullOrWhiteSpace(result.StdOut)) builder.AppendLine(result.StdOut.TrimEnd());
            if (!string.IsNullOrWhiteSpace(result.StdErr)) builder.AppendLine(result.StdErr.TrimEnd());
            await File.AppendAllTextAsync(installLogPath, builder.ToString() + Environment.NewLine, cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Preview install command failed: {resolved.Command}\n{result.StdErr}".Trim());
            }
        }

        // The install steps above ran with their own augmented PATH, but the
        // agents (Cmd nodes and AI CLI adapters) launch later with the inherited
        // host-process environment. Surface the npm global bin directory there so
        // tools an install step put on disk are actually resolvable to those nodes.
        EnsureInstalledToolsOnProcessPath();
    }

    private async Task<ManagedPreviewProcess> LaunchServiceProcessAsync(PreviewServiceConfig service, PreviewRuntime runtime, CancellationToken cancellationToken)
    {
        var resolved = BuildResolvedStep(service, runtime, service.Port);
        var logPath = Path.Combine(runtime.LogDirectory, $"{service.Name}.log");
        var writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        var writeGate = new SemaphoreSlim(1, 1);

        var process = Process.Start(BuildPreviewProcess(resolved))
            ?? throw new InvalidOperationException($"Failed to start preview service '{service.Name}'.");

        var stdoutTask = PumpStreamAsync(process.StandardOutput, writer, writeGate, cancellationToken);
        var stderrTask = PumpStreamAsync(process.StandardError, writer, writeGate, cancellationToken);

        await writer.WriteLineAsync($"> {resolved.Command}");

        return new ManagedPreviewProcess(service, process, writer, writeGate, stdoutTask, stderrTask, logPath);
    }

    private async Task StopRuntimeAsync(PreviewRuntime runtime, CancellationToken cancellationToken)
    {
        foreach (var process in runtime.Processes)
        {
            await StopProcessAsync(process, cancellationToken);
        }

        runtime.ClearProcesses();
    }

    private async Task StopProcessAsync(ManagedPreviewProcess process, CancellationToken cancellationToken)
    {
        // Claim it first: from here on the Process gets killed and disposed, so an
        // unsynchronized reader (the preview proxy) must stop treating it as live.
        process.MarkStopped();

        try
        {
            if (!process.Process.HasExited)
            {
                process.Process.Kill(entireProcessTree: true);
                await process.Process.WaitForExitAsync(cancellationToken);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop preview service {Service}", process.Service.Name);
        }

        try
        {
            await Task.WhenAll(process.StdOutPump, process.StdErrPump);
        }
        catch
        {
            // Ignore log pump failures during shutdown.
        }

        process.Writer.Dispose();
        process.WriteGate.Dispose();
        process.Process.Dispose();
    }

    private WorktreePreviewResponse BuildResponse(LoadedPreviewConfig loaded, PreviewRuntime runtime)
    {
        // List every service in the profile — not just the ones with a live process —
        // so the Preview tab can show and individually start services that are stopped.
        // Started services map from their process; the rest report as stopped with the
        // runtime's already-allocated port.
        var profileServices = loaded.Config?.Preview?.Profiles.TryGetValue(runtime.ProfileName, out var profile) == true
            ? profile.Services
            : runtime.Processes.Select(p => p.Service).ToList();

        // The bare wi-{id} hostname names one service, so it can only be advertised
        // when the profile has exactly one public service. With more than one, each
        // is advertised under its own wi-{id}-{name} instead — otherwise both would
        // be handed the same URL and one of them would reach the wrong application.
        var singlePublicService = profileServices.Count(service => service.Public) == 1;

        var serviceResponses = profileServices.Select(service =>
        {
            var process = runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, service.Name, StringComparison.OrdinalIgnoreCase));
            return process != null
                ? BuildServiceResponse(process, runtime, singlePublicService)
                : BuildStoppedServiceResponse(service, runtime);
        }).ToList();

        return new WorktreePreviewResponse
        {
            Configured = true,
            State = ComputeRuntimeState(serviceResponses),
            WorktreePath = runtime.WorktreePath,
            ConfigPath = loaded.ConfigPath,
            ProfileName = runtime.ProfileName,
            PublicHost = runtime.PublicHost,
            StateDirectory = runtime.StateDirectory,
            Services = serviceResponses,
        };
    }

    private WorktreePreviewServiceResponse BuildServiceResponse(ManagedPreviewProcess process, PreviewRuntime runtime, bool singlePublicService)
    {
        var healthUrl = ResolveHealthUrl(process.Service, runtime);
        var port = runtime.Ports.TryGetValue(process.Service.Port, out var allocated) ? allocated : (int?)null;
        var publicUrl = process.Service.Public ? BuildPublicUrl(process.Service, runtime, port, singlePublicService) : null;

        return new WorktreePreviewServiceResponse
        {
            Name = process.Service.Name,
            PortAlias = process.Service.Port,
            Status = process.Process.HasExited ? "exited" : "running",
            Port = port,
            SuggestedPort = process.Service.SuggestedPort,
            HealthUrl = healthUrl,
            PublicUrl = publicUrl,
            LogFilePath = process.LogFilePath,
            ProcessId = process.Process.Id,
            ExitCode = process.Process.HasExited ? process.Process.ExitCode : null,
        };
    }

    /// <summary>
    /// The URL a human is handed for a <c>"public": true</c> service, in strict
    /// precedence: the service's own <c>publicUrl</c> template wins (an author who
    /// spelled out a URL means it); then the preview proxy origin, which is the
    /// only form reachable when ILD runs behind an ingress that never published the
    /// service's port; then the historical direct <c>http://{publicHost}:{port}</c>.
    /// With <c>ILD_PREVIEW_PROXY_BASE</c> unset the middle rung disappears and the
    /// result is exactly what it has always been.
    /// <para>
    /// <paramref name="singlePublicService"/> decides whether the bare
    /// <c>wi-{id}</c> hostname is this service's to claim; when it is not, the
    /// service is advertised as <c>wi-{id}-{name}</c>. A name that is not a legal
    /// DNS label cannot appear in a hostname at all, so such a service falls back
    /// to the direct URL rather than being handed one that cannot resolve.
    /// </para>
    /// </summary>
    private string? BuildPublicUrl(PreviewServiceConfig service, PreviewRuntime runtime, int? port, bool singlePublicService)
    {
        var authored = ResolveOptionalTemplate(service.PublicUrl, runtime, service.Port);
        if (authored != null)
            return authored;

        var proxyLabel = BuildPreviewHostLabel(runtime.WorkItemId, singlePublicService ? null : service.Name);
        if (_proxyBase.Enabled && proxyLabel != null)
            return _proxyBase.BuildUrl(proxyLabel);

        return port is int allocated ? $"http://{runtime.PublicHost}:{allocated}" : null;
    }

    /// <summary>
    /// The hostname label for a work item's preview, or null when one cannot be
    /// formed — no work item id, or a service name that is not a legal DNS label
    /// (which <c>ild.config.json</c> does not otherwise constrain).
    /// </summary>
    private static string? BuildPreviewHostLabel(string? workItemId, string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            return null;

        if (serviceName == null)
            return $"wi-{workItemId}";

        return IsDnsLabelSegment(serviceName) ? $"wi-{workItemId}-{serviceName}" : null;
    }

    private static bool IsDnsLabelSegment(string value)
        => value.Length > 0
            && value[0] != '-'
            && value[^1] != '-'
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // A configured-but-not-running service in an otherwise live runtime. Health and
    // public URLs are left null: their templates can reference other services' ports
    // and resolving them for a service that isn't up adds no value.
    private static WorktreePreviewServiceResponse BuildStoppedServiceResponse(PreviewServiceConfig service, PreviewRuntime runtime)
        => new()
        {
            Name = service.Name,
            PortAlias = service.Port,
            Status = "stopped",
            Port = runtime.Ports.TryGetValue(service.Port, out var port) ? port : null,
            SuggestedPort = service.SuggestedPort,
        };

    private static string ComputeRuntimeState(IReadOnlyList<WorktreePreviewServiceResponse> services)
    {
        if (services.Count == 0)
            return "stopped";
        if (services.Any(s => string.Equals(s.Status, "exited", StringComparison.OrdinalIgnoreCase)))
            return "failed";
        if (services.All(s => string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase)))
            return "running";
        if (services.Any(s => string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase)))
            return "partial";
        return "stopped";
    }

    private WorktreePreviewResponse BuildStoppedResponse(
        LoadedPreviewConfig loaded,
        string? profileName = null,
        string? publicHost = null,
        string? stateDirectory = null,
        string? configPath = null)
    {
        var resolvedProfileName = profileName ?? (loaded.Configured && loaded.Config != null ? SelectProfileName(loaded.Config, null) : null);
        var stoppedServices = resolvedProfileName != null && loaded.Config?.Preview?.Profiles.TryGetValue(resolvedProfileName, out var profile) == true
            ? profile.Services.Select(service => new WorktreePreviewServiceResponse
            {
                Name = service.Name,
                PortAlias = service.Port,
                Status = "stopped",
                SuggestedPort = service.SuggestedPort,
            }).ToList()
            : new List<WorktreePreviewServiceResponse>();

        return new WorktreePreviewResponse
        {
            Configured = loaded.Configured,
            State = loaded.Configured ? "stopped" : "notConfigured",
            WorktreePath = loaded.WorktreePath,
            ConfigPath = configPath ?? loaded.ConfigPath,
            ProfileName = resolvedProfileName,
            PublicHost = publicHost,
            StateDirectory = stateDirectory,
            Message = loaded.Message,
            Services = stoppedServices,
        };
    }

    private static async Task PumpStreamAsync(StreamReader reader, StreamWriter writer, SemaphoreSlim writeGate, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                await writeGate.WaitAsync(cancellationToken);
                try
                {
                    await writer.WriteLineAsync(line);
                }
                finally
                {
                    writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during teardown.
        }
        catch (ObjectDisposedException)
        {
            // Ignore disposal races during teardown.
        }
    }

    private async Task WaitForHealthAsync(string serviceName, string healthUrl, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(healthUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // Retry until timeout.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException($"Preview service '{serviceName}' did not become healthy at {healthUrl}.");
    }

    /// <summary>
    /// Per-worktree preview state the <em>preview itself</em> writes — the npm cache
    /// its install steps populate, and whatever a profile puts under
    /// <c>${STATE_DIR}</c>. Rooted at the shared scratch root: the setgid,
    /// shared-group tree with a default ACL that the entrypoint provisions for
    /// exactly this. The preview's steps run as the agent (ADR-0016) and must be
    /// able to write here, which the orchestrator-private root would not allow.
    ///
    /// <para>
    /// The old private-root rationale — the agent can predict this path, so it could
    /// pre-create it and plant content the steps then consume — does not survive the
    /// steps becoming the agent: the agent planting something its own uid then reads
    /// crosses no boundary. It survives for anything the <em>orchestrator</em>
    /// touches, which is why the log files are not here; see
    /// <see cref="BuildLogDirectory"/>. What this does cost is that preview state is
    /// reachable by every run's agent through the shared <c>ild-agents</c> group —
    /// the residual ADR-0014 already states for every other shared tree, not a new
    /// one.
    /// </para>
    /// </summary>
    private static string BuildStateDirectory(string worktreePath)
        => AgentIsolation.CreateScratchDirectory("preview", WorktreeSlug(worktreePath));

    /// <summary>
    /// Where the per-service and install logs go. Deliberately <em>not</em> the
    /// shared state directory: these files are opened, appended to and read back by
    /// the orchestrator — the stdout/stderr pumps
    /// (<see cref="LaunchServiceProcessAsync"/>, <see cref="RunInstallStepsAsync"/>)
    /// run in-process, and <see cref="GetServiceLogAsync"/> serves them to
    /// <c>get_preview_logs</c>. Moving the preview's steps to the agent uid did
    /// nothing to move those.
    ///
    /// <para>
    /// So the private root's reasoning applies here undiminished. The path is a hash
    /// of the worktree, which the agent knows, and a shared-group directory would let
    /// it pre-create <c>install.log</c> as a symlink to somewhere only the
    /// orchestrator can write — <c>~/.profile</c>, say, which the next
    /// <c>/bin/sh -lc</c> preview spawn would then execute as the orchestrator. The
    /// read side is the mirror: an arbitrary orchestrator-readable file, served
    /// through the API. The <c>0700</c> root, created before any agent-uid process
    /// runs, is what forecloses both; it is the same guarantee the git askpass helper
    /// relies on (ADR-0014).
    /// </para>
    /// </summary>
    private static string BuildLogDirectory(string worktreePath)
        => AgentIsolation.CreatePrivateDirectory("preview", WorktreeSlug(worktreePath));

    private static string WorktreeSlug(string worktreePath)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(worktreePath))).ToLowerInvariant();

    private ResolvedStep BuildResolvedStep(PreviewCommandConfig step, PreviewRuntime runtime, string? currentPortAlias)
    {
        var workingDirectory = ResolveWorkingDirectory(step.Cwd, runtime, currentPortAlias);
        var environment = BuildDefaultEnvironment(runtime.StateDirectory);

        // Precedence: base defaults < repo custom .env < per-service ild.config env.
        // The repo .env is a repo-wide baseline; committed per-service config wins.
        // Custom values are injected verbatim — they are user-authored secrets, not
        // ${PORT:...} templates, so they must not go through ResolveTemplate.
        foreach (var entry in runtime.CustomEnv)
        {
            environment[entry.Key] = entry.Value;
        }

        if (step.Env != null)
        {
            foreach (var entry in step.Env)
            {
                environment[entry.Key] = ResolveTemplate(entry.Value, runtime, currentPortAlias);
            }
        }

        return new ResolvedStep(
            ResolveTemplate(step.Command, runtime, currentPortAlias),
            workingDirectory,
            environment);
    }

    /// <summary>
    /// The base environment every preview step starts from, before the repository's
    /// <c>.env</c> and the service's own <c>env</c> are layered over it. Takes the
    /// state directory rather than the whole runtime because that is the only part
    /// it needs — which also makes what a preview child's <c>HOME</c> and npm prefix
    /// resolve to assertable without standing up a runtime.
    /// </summary>
    internal Dictionary<string, string> BuildDefaultEnvironment(string stateDirectory)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var home = ResolveHomeDirectory();
        var npmPrefix = Path.Combine(home, ".local");
        var npmBin = GetNpmGlobalBinDirectory();
        var npmCache = Path.Combine(stateDirectory, "npm-cache");

        EnsureNpmDirectory(npmPrefix);
        EnsureNpmDirectory(npmBin);
        Directory.CreateDirectory(npmCache);

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        environment["HOME"] = home;
        environment["NPM_CONFIG_PREFIX"] = npmPrefix;
        environment["NPM_CONFIG_CACHE"] = npmCache;
        environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
            ? npmBin
            : $"{npmBin}{Path.PathSeparator}{currentPath}";

        return environment;
    }

    /// <summary>
    /// The <c>HOME</c> a preview step runs with, and the root the npm global prefix
    /// is derived from. Under uid isolation that is the <em>agent's</em> home:
    /// <see cref="AgentIsolation.Route(ProcessStartInfo, string?, string?, string?)"/>
    /// sets <c>HOME</c> there as part of the crossing, so deriving the prefix from
    /// anything else would point <c>npm install -g</c> at a directory the uid
    /// running it cannot write — the orchestrator's home is <c>0710</c>,
    /// traverse-only for the agent.
    /// </summary>
    private string ResolveHomeDirectory()
    {
        if (ChildHome is { } childHome)
            return childHome;

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            // Not reachable in the container (HOME is always set), and only
            // reachable at all when isolation is off — so this is the single-uid
            // fallback. It must still not be a fixed path in world-writable /tmp:
            // the step's npm would read a planted ~/.npmrc. Same reasoning as the
            // askpass helper (ADR-0014).
            return AgentIsolation.CreatePrivateDirectory("home");
        }

        Directory.CreateDirectory(home);
        return home;
    }

    /// <summary>
    /// The directory where <c>npm install -g</c> places executables during an
    /// install step (<see cref="BuildDefaultEnvironment"/> points
    /// <c>NPM_CONFIG_PREFIX</c> at <c>$HOME/.local</c>, so global CLIs such as
    /// <c>vp</c> land in <c>$HOME/.local/bin</c>).
    /// </summary>
    private string GetNpmGlobalBinDirectory()
        => Path.Combine(ResolveHomeDirectory(), ".local", "bin");

    /// <summary>
    /// Create one of the npm prefix directories — but never inside the agent's home.
    /// The entrypoint provisions that home's scaffolding as the agent uid; a
    /// directory the orchestrator created there would be owned by the orchestrator,
    /// in a home whose group the agent is not a member of, and the agent's own
    /// <c>npm install -g</c> would then fail on a prefix it cannot write. There, the
    /// orchestrator only <em>names</em> the path.
    ///
    /// <para>
    /// The test is containment in the home the child is actually given, not "is
    /// isolation on" — those are different questions when a crossing is configured
    /// with a user but no home. The prefix then lands in the orchestrator's own
    /// home, where the orchestrator both may and must create it: skipping would
    /// leave <see cref="EnsureInstalledToolsOnProcessPath"/> advertising a
    /// directory that does not exist.
    /// </para>
    /// </summary>
    private void EnsureNpmDirectory(string path)
    {
        if (ChildHome is { } childHome && IsInside(path, childHome))
            return;

        Directory.CreateDirectory(path);
    }

    private static bool IsInside(string path, string root)
    {
        // Resolve the root through any symlinks first. Path.GetFullPath normalizes
        // ".." but not links, so a symlinked agent home would compare against a name
        // that is not where the directory actually lives and the containment test
        // could answer wrong in either direction.
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Resolve(root))) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(Resolve(path)).StartsWith(prefix, StringComparison.Ordinal);

        static string Resolve(string value)
        {
            try
            {
                // Only an existing entry can be resolved; a path yet to be created
                // has no link to follow, so its literal form is already the answer.
                return new DirectoryInfo(value).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? value;
            }
            catch (IOException) { return value; }
            catch (UnauthorizedAccessException) { return value; }
        }
    }

    /// <summary>
    /// Add the npm global bin directory to the host process PATH so the agents can
    /// resolve tools an install step put there. Cmd nodes and the AI CLI adapters
    /// launch their processes with the inherited host-process environment, which
    /// does not otherwise include <c>$HOME/.local/bin</c>; without this,
    /// <c>npm install -g</c>'d binaries are invisible to every node that runs after
    /// the Start node's install. Idempotent.
    ///
    /// <para>
    /// Under uid isolation both ends of that hand-off are the agent — the install
    /// step that writes the tool and the nodes that later exec it — so the prefix
    /// following the agent's home makes this path exactly right rather than
    /// approximately: before, a binary under the orchestrator's
    /// <c>/home/ild/.local/bin</c> was executable by the agent only by accident of
    /// mode bits.
    /// </para>
    ///
    /// <para>
    /// It is <em>appended</em>, not prepended. This is the orchestrator's own PATH,
    /// and under isolation the directory being added is agent-writable, so a planted
    /// binary sharing a name with something the image ships would otherwise be
    /// preferred by every orchestrator-side spawn that resolves by bare name — git
    /// and npm through <c>ProcessRunner</c>, for instance. Appending means the
    /// image's copy wins ties and only genuinely new tools are contributed, which is
    /// all this was ever for. (The preview's own children still get it first: they
    /// run as the agent, where a tool it installed beating one in the image is the
    /// intended behaviour and crosses no boundary.)
    /// </para>
    /// </summary>
    private void EnsureInstalledToolsOnProcessPath()
    {
        var npmBin = GetNpmGlobalBinDirectory();

        // Create the directory up front. It is only populated lazily (by an
        // `npm install -g` in a Start node), but agents launched with this PATH
        // — e.g. Claude Code — warn when a PATH entry does not exist on disk.
        // Under isolation the entrypoint has already created it as the agent; see
        // EnsureNpmDirectory for why the orchestrator must not.
        EnsureNpmDirectory(npmBin);

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        var alreadyPresent = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, npmBin, StringComparison.Ordinal));
        if (alreadyPresent)
            return;

        Environment.SetEnvironmentVariable(
            "PATH",
            string.IsNullOrWhiteSpace(currentPath) ? npmBin : $"{currentPath}{Path.PathSeparator}{npmBin}");
    }

    private string ResolveHealthUrl(PreviewServiceConfig service, PreviewRuntime runtime)
    {
        return ResolveTemplate(service.HealthUrl!, runtime, service.Port);
    }

    private string ResolveWorkingDirectory(string? cwd, PreviewRuntime runtime, string? currentPortAlias)
    {
        var resolved = string.IsNullOrWhiteSpace(cwd)
            ? runtime.WorktreePath
            : ResolveTemplate(cwd, runtime, currentPortAlias);

        var full = Path.GetFullPath(Path.IsPathRooted(resolved)
            ? resolved
            : Path.Combine(runtime.WorktreePath, resolved));

        if (!full.StartsWith(runtime.WorktreePath, StringComparison.Ordinal)
            && !full.StartsWith(runtime.StateDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Preview cwd '{resolved}' escapes the worktree/state directory boundary.");
        }

        Directory.CreateDirectory(full);
        return full;
    }

    private string ResolveTemplate(string value, PreviewRuntime runtime, string? currentPortAlias)
    {
        return TemplateTokenRegex.Replace(value, match =>
        {
            var token = match.Groups[1].Value;
            if (string.Equals(token, "WORKTREE", StringComparison.OrdinalIgnoreCase))
                return runtime.WorktreePath;
            if (string.Equals(token, "STATE_DIR", StringComparison.OrdinalIgnoreCase))
                return runtime.StateDirectory;
            if (string.Equals(token, "HOST", StringComparison.OrdinalIgnoreCase))
                return "0.0.0.0";
            if (string.Equals(token, "PUBLIC_HOST", StringComparison.OrdinalIgnoreCase))
                return runtime.PublicHost;
            if (string.Equals(token, "PORT", StringComparison.OrdinalIgnoreCase))
            {
                if (currentPortAlias == null || !runtime.Ports.TryGetValue(currentPortAlias, out var currentPort))
                    throw new InvalidOperationException($"Template '{value}' references ${{PORT}} without a current service port.");
                return currentPort.ToString();
            }
            if (token.StartsWith("PORT:", StringComparison.OrdinalIgnoreCase))
            {
                var alias = token[5..];
                if (!runtime.Ports.TryGetValue(alias, out var namedPort))
                    throw new InvalidOperationException($"Template '{value}' references unknown port alias '{alias}'.");
                return namedPort.ToString();
            }

            throw new InvalidOperationException($"Unsupported preview template token '{token}'.");
        });
    }

    private string? ResolveOptionalTemplate(string? value, PreviewRuntime runtime, string? currentPortAlias)
    {
        return string.IsNullOrWhiteSpace(value) ? null : ResolveTemplate(value, runtime, currentPortAlias);
    }

    /// <summary>
    /// The one place a preview child's uid, capabilities and environment are
    /// decided — both spawn sites (install steps and long-running services) go
    /// through it, so neither can drift from the other.
    ///
    /// <para>
    /// The command text comes from the worktree's <c>ild.config.json</c>, a file the
    /// agent writes and can trigger itself through the ILD MCP tools. It is
    /// therefore agent-authored code, and it runs as the agent:
    /// <see cref="AgentIsolation.Route(ProcessStartInfo, string?, string?, string?)"/>
    /// drops it to the agent uid, clears the inherited and ambient capability sets,
    /// and points <c>HOME</c> at the agent's own home. That gives a preview command
    /// exactly the privileges the agent already has and nothing more — and, because
    /// the agent's own builds in the same worktree run under that uid too, it is
    /// what stops a preview build tripping over files the agent owns (ADR-0016).
    /// With <c>ILD_AGENT_USER</c> unset the routing is a no-op and the command runs
    /// inline as the current user, exactly as before.
    /// </para>
    ///
    /// <para>
    /// The environment is constructed, not inherited: .NET pre-populates a child's
    /// from the current process, which would otherwise hand a preview command the
    /// DB connection strings, the encryption-at-rest key, the orchestrator's own API
    /// tokens, and the variables describing this process's identity and private
    /// directories. Stripping happens before the resolved step's environment is
    /// applied, never after, so what it removes is only ever what was
    /// <em>inherited</em>: refusing to inherit is not refusing to be configured. A
    /// preview that legitimately needs one of those names sets it in
    /// <c>ild.config.json</c> or the repository's preview <c>.env</c>, and that
    /// value survives — pointed wherever the config points it rather than at the
    /// orchestrator's.
    /// </para>
    /// </summary>
    internal ProcessStartInfo BuildPreviewProcess(ResolvedStep resolved)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = resolved.WorkingDirectory,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(resolved.Command);

        AgentIsolation.StripOrchestratorEnvironment(psi);
        AgentIsolation.Route(psi, _agentUser, _agentGroup, _agentHome);

        foreach (var entry in resolved.Environment)
        {
            psi.Environment[entry.Key] = entry.Value;
        }

        return psi;
    }

    private async Task<CommandResult> RunCommandAsync(ResolvedStep resolved, CancellationToken cancellationToken)
    {
        using var process = Process.Start(BuildPreviewProcess(resolved))
            ?? throw new InvalidOperationException($"Failed to start command '{resolved.Command}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record LoadedPreviewConfig(bool Configured, string WorktreePath, string ConfigPath, IldWorkspaceConfig? Config, string? Message);

    internal sealed record ResolvedStep(string Command, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

    private sealed class PreviewRuntime
    {
        public PreviewRuntime(
            string worktreePath,
            string configPath,
            string profileName,
            string stateDirectory,
            string logDirectory,
            string publicHost,
            Dictionary<string, int> ports,
            List<ManagedPreviewProcess> processes,
            IReadOnlyDictionary<string, string> customEnv,
            string? workItemId = null)
        {
            WorkItemId = workItemId;
            WorktreePath = worktreePath;
            ConfigPath = configPath;
            ProfileName = profileName;
            StateDirectory = stateDirectory;
            LogDirectory = logDirectory;
            PublicHost = publicHost;
            Ports = ports;
            _processes = processes.ToArray();
            CustomEnv = customEnv;
        }

        /// <summary>
        /// The work item this worktree belongs to, when the starter knew it. Only
        /// used to build <c>wi-{id}.{proxy base}</c> public URLs; null leaves those
        /// URLs on their loopback form.
        /// </summary>
        public string? WorkItemId { get; }

        public string WorktreePath { get; }
        public string ConfigPath { get; }
        public string ProfileName { get; }

        /// <summary>Shared with the agent — see <c>BuildStateDirectory</c>.</summary>
        public string StateDirectory { get; }

        /// <summary>Orchestrator-private — see <c>BuildLogDirectory</c>.</summary>
        public string LogDirectory { get; }

        public string PublicHost { get; }
        public Dictionary<string, int> Ports { get; }

        private volatile IReadOnlyList<ManagedPreviewProcess> _processes;

        /// <summary>
        /// The service processes started for this runtime. The list is replaced
        /// wholesale on every change rather than mutated in place, so a reader always
        /// enumerates a complete snapshot and can never see the collection halfway
        /// through a modification.
        /// <para>
        /// The writers here are already serialized by the service's start/stop gate,
        /// but that gate is held across process launches and health probes — seconds
        /// to minutes — and the preview proxy reads this on <em>every</em> HTTP request
        /// to a preview hostname: every asset, XHR and reload poll. Making readers wait
        /// on the gate would stall a loading page behind a service restart, so instead
        /// the read takes no lock and the writer pays for a copy of a list that never
        /// holds more than a handful of entries.
        /// </para>
        /// </summary>
        public IReadOnlyList<ManagedPreviewProcess> Processes => _processes;

        public void AddProcess(ManagedPreviewProcess process)
            => _processes = [.. _processes, process];

        public void RemoveProcess(ManagedPreviewProcess process)
            => _processes = _processes.Where(p => !ReferenceEquals(p, process)).ToArray();

        public void ClearProcesses() => _processes = [];

        /// <summary>Parsed repository custom <c>.env</c> (see <c>Repository.PreviewEnv</c>),
        /// injected into every step's environment. Empty when none is configured.</summary>
        public IReadOnlyDictionary<string, string> CustomEnv { get; }
    }

    private sealed class ManagedPreviewProcess
    {
        public ManagedPreviewProcess(
            PreviewServiceConfig service,
            Process process,
            StreamWriter writer,
            SemaphoreSlim writeGate,
            Task stdOutPump,
            Task stdErrPump,
            string logFilePath)
        {
            Service = service;
            Process = process;
            Writer = writer;
            WriteGate = writeGate;
            StdOutPump = stdOutPump;
            StdErrPump = stdErrPump;
            LogFilePath = logFilePath;
        }

        public PreviewServiceConfig Service { get; }
        public Process Process { get; }
        public StreamWriter Writer { get; }
        public SemaphoreSlim WriteGate { get; }
        public Task StdOutPump { get; }
        public Task StdErrPump { get; }
        public string LogFilePath { get; }

        private volatile bool _stopped;

        /// <summary>
        /// Records that the teardown path has taken this process over, before it
        /// disposes <see cref="Process"/>.
        /// </summary>
        public void MarkStopped() => _stopped = true;

        /// <summary>
        /// Whether this service is up — safe to ask from any thread at any time, which
        /// <see cref="Process"/> is not: reading <c>HasExited</c> on a disposed
        /// <see cref="System.Diagnostics.Process"/> throws
        /// <see cref="InvalidOperationException"/>, and teardown can dispose it at any
        /// moment. The preview proxy asks this on every request to a preview hostname
        /// without holding the service's start/stop gate, so it cannot assume a stop in
        /// flight has either not begun or fully finished. Callers that do hold the gate
        /// may read <see cref="Process"/> directly, and do, to report exit codes.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                if (_stopped)
                    return false;

                try
                {
                    return !Process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    // Disposed between the flag check and here.
                    return false;
                }
            }
        }
    }

    private sealed class IldWorkspaceConfig
    {
        public PreviewRootConfig? Preview { get; set; }
    }

    private sealed class PreviewRootConfig
    {
        public string? DefaultProfile { get; set; }
        public Dictionary<string, PreviewProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PreviewProfileConfig
    {
        public List<PreviewCommandConfig> Install { get; set; } = new();
        public List<PreviewServiceConfig> Services { get; set; } = new();
    }

    private class PreviewCommandConfig
    {
        public string? Cwd { get; set; }
        public string Command { get; set; } = string.Empty;
        public Dictionary<string, string>? Env { get; set; }
    }

    private sealed class PreviewServiceConfig : PreviewCommandConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Port { get; set; } = string.Empty;
        public int? SuggestedPort { get; set; }
        public string? HealthUrl { get; set; }
        public bool Public { get; set; }
        public string? PublicUrl { get; set; }

        /// <summary>
        /// Whether the preview proxy replaces the <c>Host</c> header with the
        /// loopback authority it forwards to. Defaults to true because dev servers
        /// that validate the host (Vite, webpack-dev-server, Rails, Django) reject a
        /// request arriving as <c>wi-12.ild.kube</c> outright. Set false for a
        /// service that needs to see the real browser-facing host — one that builds
        /// absolute links or issues host-bound redirects — and add the preview
        /// wildcard to that service's own allowed-hosts list instead.
        /// </summary>
        public bool RewriteHost { get; set; } = true;
    }
}