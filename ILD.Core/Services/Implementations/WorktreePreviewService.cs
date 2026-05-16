using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ILD.Core.Services.Implementations;

public sealed class WorktreePreviewService : IWorktreePreviewService, IDisposable
{
    private const string ConfigFileName = "ild.config.json";
    private const int DefaultTimeoutSeconds = 600;
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

            var publicHost = string.IsNullOrWhiteSpace(options.PublicHost)
                ? (_configuration["ILD_PREVIEW_PUBLIC_HOST"] ?? "127.0.0.1")
                : options.PublicHost.Trim();

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
                ResolveTimeoutSeconds(options.TimeoutSeconds),
                new List<ManagedPreviewProcess>());

            if (!options.SkipInstall)
            {
                await RunInstallStepsAsync(profile.Install, runtime, cancellationToken);
            }

            foreach (var service in profile.Services)
            {
                runtime.Processes.Add(await StartServiceAsync(service, runtime, cancellationToken));
            }

            foreach (var service in profile.Services)
            {
                var healthUrl = ResolveHealthUrl(service, runtime);
                await WaitForHealthAsync(service.Name, healthUrl, cancellationToken);
            }

            _runtimes[normalized] = runtime;
            StartAutoStopTimer(runtime);
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
            if (string.IsNullOrWhiteSpace(service.Name))
                throw new InvalidOperationException($"Preview profile '{profileName}' has a service with no name.");
            if (string.IsNullOrWhiteSpace(service.Command))
                throw new InvalidOperationException($"Preview service '{service.Name}' has no command.");
            if (string.IsNullOrWhiteSpace(service.Port))
                throw new InvalidOperationException($"Preview service '{service.Name}' has no port alias.");
            if (!seenNames.Add(service.Name))
                throw new InvalidOperationException($"Preview profile '{profileName}' defines duplicate service name '{service.Name}'.");
            if (string.IsNullOrWhiteSpace(service.HealthUrl))
                throw new InvalidOperationException($"Preview service '{service.Name}' must define healthUrl.");
            if (service.SuggestedPort is <= 0)
                throw new InvalidOperationException($"Preview service '{service.Name}' has invalid suggestedPort '{service.SuggestedPort}'.");
        }
    }

    private static int ResolveTimeoutSeconds(int? requestedTimeoutSeconds)
    {
        if (requestedTimeoutSeconds is null)
            return DefaultTimeoutSeconds;
        if (requestedTimeoutSeconds < 0)
            throw new InvalidOperationException("Preview timeoutSeconds must be zero or greater.");

        return requestedTimeoutSeconds.Value;
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

            ports[service.Port] = FindFreePort(reservedPorts);
            reservedPorts.Add(ports[service.Port]);
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
            var timeout = TimeSpan.FromSeconds(step.TimeoutSeconds <= 0 ? 600 : step.TimeoutSeconds);
            var result = await RunCommandAsync(resolved.Command, resolved.WorkingDirectory, resolved.Environment, timeout, cancellationToken);

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
    }

    private async Task<ManagedPreviewProcess> StartServiceAsync(PreviewServiceConfig service, PreviewRuntime runtime, CancellationToken cancellationToken)
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
        runtime.AutoStopCancellation.Cancel();

        foreach (var process in runtime.Processes)
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

        runtime.Processes.Clear();
    }

    private void StartAutoStopTimer(PreviewRuntime runtime)
    {
        if (runtime.TimeoutSeconds == 0)
            return;

        runtime.AutoStopTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(runtime.TimeoutSeconds), runtime.AutoStopCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _gate.WaitAsync();
            try
            {
                if (!_runtimes.TryGetValue(runtime.WorktreePath, out var current)
                    || !ReferenceEquals(current, runtime))
                {
                    return;
                }

                _logger.LogInformation(
                    "Auto-stopping preview for {WorktreePath} after {TimeoutSeconds} seconds",
                    runtime.WorktreePath,
                    runtime.TimeoutSeconds);

                _runtimes.TryRemove(runtime.WorktreePath, out _);
                await StopRuntimeAsync(runtime, CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private WorktreePreviewResponse BuildResponse(LoadedPreviewConfig loaded, PreviewRuntime runtime)
    {
        var serviceResponses = runtime.Processes.Select(process =>
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
        }).ToList();

        var state = serviceResponses.Count == 0
            ? "stopped"
            : serviceResponses.Any(s => string.Equals(s.Status, "exited", StringComparison.OrdinalIgnoreCase))
                ? "failed"
                : "running";

        return new WorktreePreviewResponse
        {
            Configured = true,
            State = state,
            WorktreePath = runtime.WorktreePath,
            ConfigPath = loaded.ConfigPath,
            ProfileName = runtime.ProfileName,
            PublicHost = runtime.PublicHost,
            StateDirectory = runtime.StateDirectory,
            TimeoutSeconds = runtime.TimeoutSeconds,
            AutoStopAt = runtime.AutoStopAt,
            Services = serviceResponses,
        };
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
            TimeoutSeconds = DefaultTimeoutSeconds,
            AutoStopAt = null,
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

        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Path.GetTempPath(), "ild-home");
        }

        Directory.CreateDirectory(home);

        var npmPrefix = Path.Combine(home, ".local");
        var npmBin = Path.Combine(npmPrefix, "bin");
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
            : $"{npmBin}:{currentPath}";

        return environment;
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
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

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
            int timeoutSeconds,
            List<ManagedPreviewProcess> processes)
        {
            WorktreePath = worktreePath;
            ConfigPath = configPath;
            ProfileName = profileName;
            StateDirectory = stateDirectory;
            PublicHost = publicHost;
            Ports = ports;
            TimeoutSeconds = timeoutSeconds;
            AutoStopAt = timeoutSeconds == 0 ? null : DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            Processes = processes;
        }

        public string WorktreePath { get; }
        public string ConfigPath { get; }
        public string ProfileName { get; }
        public string StateDirectory { get; }
        public string PublicHost { get; }
        public Dictionary<string, int> Ports { get; }
        public int TimeoutSeconds { get; }
        public DateTimeOffset? AutoStopAt { get; }
        public CancellationTokenSource AutoStopCancellation { get; } = new();
        public Task? AutoStopTask { get; set; }
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
        public int TimeoutSeconds { get; set; } = 600;
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