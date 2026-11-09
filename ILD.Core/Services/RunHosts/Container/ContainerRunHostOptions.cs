using System.Globalization;
using System.Net;

namespace ILD.Core.Services.RunHosts.Container;

/// <summary>
/// The settings of the <c>container</c> run host. Every name is documented in <c>docs/configuration.md</c>.
/// </summary>
public sealed record ContainerRunHostOptions
{
    public const string SocketSetting = "ILD_CONTAINER_SOCKET";
    public const string RunnerImageSetting = "ILD_CONTAINER_RUNNER_IMAGE";
    public const string InstanceSetting = "ILD_CONTAINER_INSTANCE";
    public const string IldContainerSetting = "ILD_CONTAINER_ILD_CONTAINER";
    public const string SweepIntervalSetting = "ILD_CONTAINER_SWEEP_INTERVAL_MINUTES";
    public const string ReadyTimeoutSetting = "ILD_CONTAINER_READY_TIMEOUT_SECONDS";
    public const string EgressProxyPortSetting = "ILD_NETWORK_PROXY_PORT";
    public const string ToolsVolumeSetting = "ILD_CONTAINER_TOOLS_VOLUME";

    private static readonly string[] GitIdentitySettings = ["GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL"];

    /// <summary>The path of the Docker-compatible API's unix socket. Never detected: whoever holds it controls the containers of the host.</summary>
    public string Socket { get; init; } = string.Empty;

    /// <summary>Names this ILD install among others sharing the container engine; the sweeper touches only objects of its own.</summary>
    public string Instance { get; init; } = "default";

    public string RunnerImage { get; init; } = string.Empty;

    /// <summary>The name of ILD's own container when ILD runs in one; null when it runs on the host.</summary>
    public string? IldContainer { get; init; }

    /// <summary>What a run may use, as a CPU count (<c>4</c>, <c>500m</c>) or null for no limit.</summary>
    public long? NanoCpus { get; init; } = 4_000_000_000;

    /// <summary>The memory a run may use in bytes, or null for no limit.</summary>
    public long? MemoryBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>The port of ILD's egress proxy; 0 when there is none.</summary>
    public int EgressProxyPort { get; init; }

    /// <summary>The git author and committer identity ILD was given, which the container has no <c>.gitconfig</c> to take from.</summary>
    public IReadOnlyDictionary<string, string> GitIdentity { get; init; } = new Dictionary<string, string>();

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>ILD's address as the run reaches it (<c>ILD_API_URL</c>): a host name the run's container is given the address of ILD for.</summary>
    public string IldUrl { get; init; } = string.Empty;

    /// <summary>Whether runs get toolchains and agents from the store (<see cref="RunHostOptions.ToolchainsSetting"/>).</summary>
    public bool Toolchains { get; init; } = true;

    /// <summary>The named volume that holds the toolchain store, shared by every run and ILD on the engine.</summary>
    public string ToolsVolume { get; init; } = "ild-tools";

    /// <summary>The host name in <see cref="IldUrl"/>.</summary>
    public string IldHost => new Uri(IldUrl).Host;

    public static ContainerRunHostOptions From(Func<string, string?> setting)
    {
        string? Value(string name) => string.IsNullOrWhiteSpace(setting(name)) ? null : setting(name)!.Trim();

        var defaults = new ContainerRunHostOptions();
        var socket = Value(SocketSetting)
            ?? throw new InvalidOperationException($"{SocketSetting} is required when ILD runs from its image outside Kubernetes: it is the socket of the container engine, and ILD never looks for one.");
        const string UnixPrefix = "unix://";
        if (socket.StartsWith(UnixPrefix, StringComparison.Ordinal)) socket = socket[UnixPrefix.Length..];
        if (!socket.StartsWith('/'))
            throw new InvalidOperationException($"{SocketSetting} is '{socket}'; it must be the absolute path of a unix socket.");

        var image = Value(RunnerImageSetting)
            ?? throw new InvalidOperationException($"{RunnerImageSetting} is required when ILD runs from its image outside Kubernetes.");

        var ildUrl = Value("ILD_API_URL")
            ?? throw new InvalidOperationException("ILD_API_URL must be set when ILD runs from its image: the run's container reaches the model gateway and the MCP endpoint there.");
        if (!Uri.TryCreate(ildUrl, UriKind.Absolute, out var uri) || uri.IsLoopback || IPAddress.TryParse(uri.Host, out _))
            throw new InvalidOperationException(
                $"ILD_API_URL is '{ildUrl}'; with runs in containers it must name ILD by a host name that is not localhost (the machine's name, or the compose service), because each run's container is given that name for ILD's address on its own network.");

        return defaults with
        {
            Socket = socket,
            Instance = DockerName(Value(InstanceSetting), InstanceSetting) ?? defaults.Instance,
            RunnerImage = image,
            IldContainer = Value(IldContainerSetting),
            NanoCpus = Value(RunHostOptions.CpuLimitSetting) is { } cpu ? NanoCpusOf(cpu) : defaults.NanoCpus,
            MemoryBytes = Value(RunHostOptions.MemoryLimitSetting) is { } memory ? BytesOf(memory) : defaults.MemoryBytes,
            EgressProxyPort = int.TryParse(Value(EgressProxyPortSetting), out var proxyPort) ? proxyPort : 0,
            SweepInterval = TimeSpan.FromMinutes(PositiveInt(Value(SweepIntervalSetting), SweepIntervalSetting) ?? 10),
            ReadyTimeout = TimeSpan.FromSeconds(PositiveInt(Value(ReadyTimeoutSetting), ReadyTimeoutSetting) ?? 120),
            GitIdentity = GitIdentitySettings.Select(name => (name, value: Value(name))).Where(e => e.value is not null).ToDictionary(e => e.name, e => e.value!),
            IldUrl = ildUrl,
            Toolchains = RunHostOptions.ToolchainsOn(Value(RunHostOptions.ToolchainsSetting)),
            ToolsVolume = DockerName(Value(ToolsVolumeSetting), ToolsVolumeSetting) ?? defaults.ToolsVolume,
        };
    }

    /// <summary>The CPU limit of a Kubernetes-style quantity (<c>4</c>, <c>1.5</c>, <c>500m</c>) in billionths of a CPU; null for <c>0</c>, which means no limit.</summary>
    internal static long? NanoCpusOf(string quantity)
    {
        var millis = quantity.EndsWith('m');
        var number = millis ? quantity[..^1] : quantity;
        if (!decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidOperationException($"{RunHostOptions.CpuLimitSetting} is '{quantity}'; it must be a number of CPUs such as 4, 1.5 or 500m, or 0 for no limit.");
        var nano = (long)(value * (millis ? 1_000_000m : 1_000_000_000m));
        return nano == 0 ? null : nano;
    }

    /// <summary>The memory limit of a Kubernetes-style quantity (<c>8Gi</c>, <c>512Mi</c>, <c>1G</c>, a plain byte count) in bytes; null for <c>0</c>, which means no limit.</summary>
    internal static long? BytesOf(string quantity)
    {
        (string Suffix, long Factor)[] units =
        [
            ("Ki", 1L << 10), ("Mi", 1L << 20), ("Gi", 1L << 30), ("Ti", 1L << 40),
            ("K", 1_000L), ("M", 1_000_000L), ("G", 1_000_000_000L), ("T", 1_000_000_000_000L),
        ];
        var (suffix, factor) = units.FirstOrDefault(u => quantity.EndsWith(u.Suffix, StringComparison.Ordinal));
        var number = suffix is null ? quantity : quantity[..^suffix.Length];
        if (!decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidOperationException($"{RunHostOptions.MemoryLimitSetting} is '{quantity}'; it must be an amount of memory such as 8Gi or 512Mi, or 0 for no limit.");
        var bytes = (long)(value * (suffix is null ? 1 : factor));
        return bytes == 0 ? null : bytes;
    }

    // Both name engine objects and sit in label filters, so they must be a name the engine accepts. The 50-character cap is ILD's own,
    // to leave room for the prefixes and suffixes of the names made from them.
    private static string? DockerName(string? value, string setting)
        => value is null || System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,49}$")
            ? value
            : throw new InvalidOperationException($"{setting} is '{value}'; it must be letters, digits, '_', '.' and '-', starting with a letter or digit, and at most 50 characters.");

    private static int? PositiveInt(string? value, string name)
    {
        if (value is null) return null;
        return int.TryParse(value, out var n) && n > 0
            ? n
            : throw new InvalidOperationException($"{name} is '{value}'; it must be a positive whole number.");
    }
}
