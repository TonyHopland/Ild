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

    private readonly ConcurrentDictionary<string, PreviewRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorktreePreviewService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public WorktreePreviewService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<WorktreePreviewService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

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
                runtime.Processes.Add(await LaunchServiceProcessAsync(service, runtime, cancellationToken));
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
                    runtime.Processes.Remove(existing);
                }

                EnsureServicePortAllocated(service, runtime, options.PortOverrides);
            }
            else
            {
                runtime = await CreateRuntimeAsync(normalized, loaded, profileName, profile, options, cancellationToken);
                _runtimes[normalized] = runtime;
            }

            runtime.Processes.Add(await LaunchServiceProcessAsync(service, runtime, cancellationToken));
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
                runtime.Processes.Remove(process);
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
        var logPath = Path.Combine(BuildStateDirectory(normalized), $"{serviceName}.log");
        if (!File.Exists(logPath))
            return null;

        await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (maxBytes > 0 && stream.Length > maxBytes)
            stream.Seek(-maxBytes, SeekOrigin.End);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<WorktreeInstallResult> InstallAsync(string worktreePath, string? profileName = null, CancellationToken cancellationToken = default)
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

        // Install needs no ports or running services — build a port-less runtime so
        // the shared install runner resolves ${WORKTREE}/${STATE_DIR} the same way
        // the preview start path does.
        var runtime = new PreviewRuntime(
            normalized,
            loaded.ConfigPath!,
            resolvedProfileName,
            stateDirectory,
            _configuration["ILD_PREVIEW_PUBLIC_HOST"] ?? "127.0.0.1",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new List<ManagedPreviewProcess>());

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

        var ports = AllocatePorts(profile, options.PortOverrides);
        var runtime = new PreviewRuntime(
            normalized,
            loaded.ConfigPath!,
            profileName,
            stateDirectory,
            publicHost,
            ports,
            new List<ManagedPreviewProcess>());

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

        var installLogPath = Path.Combine(runtime.StateDirectory, "install.log");
        foreach (var step in installSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Command))
                continue;

            var resolved = BuildResolvedStep(step, runtime, null);
            var result = await RunCommandAsync(resolved.Command, resolved.WorkingDirectory, resolved.Environment, cancellationToken);

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
        var logPath = Path.Combine(runtime.StateDirectory, $"{service.Name}.log");
        var writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        var writeGate = new SemaphoreSlim(1, 1);

        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = resolved.WorkingDirectory,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(resolved.Command);
        foreach (var entry in resolved.Environment)
        {
            psi.Environment[entry.Key] = entry.Value;
        }

        var process = Process.Start(psi)
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

        runtime.Processes.Clear();
    }

    private async Task StopProcessAsync(ManagedPreviewProcess process, CancellationToken cancellationToken)
    {
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

        var serviceResponses = profileServices.Select(service =>
        {
            var process = runtime.Processes.FirstOrDefault(p => string.Equals(p.Service.Name, service.Name, StringComparison.OrdinalIgnoreCase));
            return process != null
                ? BuildServiceResponse(process, runtime)
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

    private WorktreePreviewServiceResponse BuildServiceResponse(ManagedPreviewProcess process, PreviewRuntime runtime)
    {
        var healthUrl = ResolveHealthUrl(process.Service, runtime);
        var publicUrl = process.Service.Public
            ? ResolveOptionalTemplate(process.Service.PublicUrl, runtime, process.Service.Port)
                ?? $"http://{runtime.PublicHost}:{runtime.Ports[process.Service.Port]}"
            : null;

        return new WorktreePreviewServiceResponse
        {
            Name = process.Service.Name,
            PortAlias = process.Service.Port,
            Status = process.Process.HasExited ? "exited" : "running",
            Port = runtime.Ports.TryGetValue(process.Service.Port, out var port) ? port : null,
            SuggestedPort = process.Service.SuggestedPort,
            HealthUrl = healthUrl,
            PublicUrl = publicUrl,
            LogFilePath = process.LogFilePath,
            ProcessId = process.Process.Id,
            ExitCode = process.Process.HasExited ? process.Process.ExitCode : null,
        };
    }

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

    private static string BuildStateDirectory(string worktreePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "ild-preview");
        Directory.CreateDirectory(root);
        var slug = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(worktreePath))).ToLowerInvariant();
        return Path.Combine(root, slug);
    }

    private ResolvedStep BuildResolvedStep(PreviewCommandConfig step, PreviewRuntime runtime, string? currentPortAlias)
    {
        var workingDirectory = ResolveWorkingDirectory(step.Cwd, runtime, currentPortAlias);
        var environment = BuildDefaultEnvironment(runtime);
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

    private static Dictionary<string, string> BuildDefaultEnvironment(PreviewRuntime runtime)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var home = ResolveHomeDirectory();
        var npmPrefix = Path.Combine(home, ".local");
        var npmBin = GetNpmGlobalBinDirectory();
        var npmCache = Path.Combine(runtime.StateDirectory, "npm-cache");

        Directory.CreateDirectory(npmPrefix);
        Directory.CreateDirectory(npmBin);
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

    private static string ResolveHomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Path.GetTempPath(), "ild-home");
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
    private static string GetNpmGlobalBinDirectory()
        => Path.Combine(ResolveHomeDirectory(), ".local", "bin");

    /// <summary>
    /// Prepend the npm global bin directory onto the host process PATH so the
    /// agents can resolve tools an install step put there. Cmd nodes and the AI
    /// CLI adapters launch their processes with the inherited host-process
    /// environment, which does not otherwise include <c>$HOME/.local/bin</c>;
    /// without this, <c>npm install -g</c>'d binaries are invisible to every
    /// node that runs after the Start node's install. Idempotent.
    /// </summary>
    private static void EnsureInstalledToolsOnProcessPath()
    {
        var npmBin = GetNpmGlobalBinDirectory();

        // Create the directory up front. It is only populated lazily (by an
        // `npm install -g` in a Start node), but agents launched with this PATH
        // — e.g. Claude Code — warn when a PATH entry does not exist on disk.
        Directory.CreateDirectory(npmBin);

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        var alreadyPresent = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, npmBin, StringComparison.Ordinal));
        if (alreadyPresent)
            return;

        Environment.SetEnvironmentVariable(
            "PATH",
            string.IsNullOrWhiteSpace(currentPath) ? npmBin : $"{npmBin}{Path.PathSeparator}{currentPath}");
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

    private static async Task<CommandResult> RunCommandAsync(
        string command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);
        foreach (var entry in environment)
        {
            psi.Environment[entry.Key] = entry.Value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start command '{command}'.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record LoadedPreviewConfig(bool Configured, string WorktreePath, string ConfigPath, IldWorkspaceConfig? Config, string? Message);

    private sealed record ResolvedStep(string Command, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
    private sealed record CommandResult(int ExitCode, string StdOut, string StdErr);

    private sealed class PreviewRuntime
    {
        public PreviewRuntime(
            string worktreePath,
            string configPath,
            string profileName,
            string stateDirectory,
            string publicHost,
            Dictionary<string, int> ports,
            List<ManagedPreviewProcess> processes)
        {
            WorktreePath = worktreePath;
            ConfigPath = configPath;
            ProfileName = profileName;
            StateDirectory = stateDirectory;
            PublicHost = publicHost;
            Ports = ports;
            Processes = processes;
        }

        public string WorktreePath { get; }
        public string ConfigPath { get; }
        public string ProfileName { get; }
        public string StateDirectory { get; }
        public string PublicHost { get; }
        public Dictionary<string, int> Ports { get; }
        public List<ManagedPreviewProcess> Processes { get; }
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
    }
}