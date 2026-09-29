namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// Whether NuGet in a run's processes will find the Azure Artifacts credential
/// provider, the only thing that turns <c>VSS_NUGET_EXTERNAL_FEED_ENDPOINTS</c>
/// into credentials. Without it a restore from a private feed fails with a 401
/// however good the PAT is, so it is looked for the way NuGet looks: the plugin
/// paths when set (they switch every other lookup off); otherwise the .NET tool's
/// <c>nuget-plugin-*</c> command on <c>PATH</c>, which only an SDK from 9.0.200 on
/// looks for, then <c>~/.nuget/plugins</c> in the home the processes run with.
/// Where no .NET SDK is installed nothing restores NuGet packages, so nothing is
/// missing either.
/// </summary>
public static class NuGetCredentialProvider
{
    /// <summary>The command the <c>Microsoft.Artifacts.CredentialProvider.NuGet.Tool</c> tool installs.</summary>
    public const string ToolCommand = "nuget-plugin-microsoft-artifacts-credential-provider";

    private const string PluginFile = "CredentialProvider.Microsoft.dll";

    // NuGet looks for nuget-plugin-* commands on PATH from this SDK on.
    private static readonly Version PathLookupSdk = new(9, 0, 200);

    public const string MissingWarning =
        "NuGet restores won't be authenticated: the Azure Artifacts credential provider was not found. npm feeds are unaffected.";

    /// <summary>True when a .NET SDK is installed and its NuGet will not find the credential provider.</summary>
    public static bool IsMissing(IProcessEnvironment environment)
        => LatestSdk(environment) is { } sdk && Locate(environment, sdk) is null;

    /// <summary>The provider a run process's NuGet would use, or null when it has none it can run.</summary>
    public static string? Locate(IProcessEnvironment environment)
        => LatestSdk(environment) is { } sdk ? Locate(environment, sdk) : null;

    private static string? Locate(IProcessEnvironment environment, Version sdk)
    {
        var agentUser = environment.Get(AgentIsolation.AgentUserEnvVar);
        var configured = environment.Get("NUGET_NETCORE_PLUGIN_PATHS") ?? environment.Get("NUGET_PLUGIN_PATHS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(path => Path.GetFileName(path) is PluginFile or ToolCommand
                    && UsableByRunProcesses(path, agentUser, executable: Path.GetFileName(path) == ToolCommand));
        }

        if (sdk >= PathLookupSdk)
        {
            var onPath = (environment.Get("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(directory => Path.Combine(directory, ToolCommand))
                .FirstOrDefault(path => UsableByRunProcesses(path, agentUser, executable: true));
            if (onPath is not null)
                return onPath;
        }

        var home = AgentIsolation.ResolveChildHome(agentUser, environment.Get(AgentIsolation.AgentHomeEnvVar))
            ?? environment.Get("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;
        var plugin = Path.Combine(home, ".nuget", "plugins", "netcore", "CredentialProvider.Microsoft", PluginFile);
        return UsableByRunProcesses(plugin, agentUser, executable: false) ? plugin : null;
    }

    // Under uid isolation the run processes are another uid, and this process can
    // only vouch for a file that everyone may read (and run, for the command), as
    // the image's copy and an installer's copy in the agent's home allow.
    private static bool UsableByRunProcesses(string path, string? agentUser, bool executable)
    {
        if (!File.Exists(path))
            return false;
        if (string.IsNullOrWhiteSpace(agentUser) || OperatingSystem.IsWindows())
            return true;
        var mode = File.GetUnixFileMode(path);
        return mode.HasFlag(UnixFileMode.OtherRead) && (!executable || mode.HasFlag(UnixFileMode.OtherExecute));
    }

    /// <summary>
    /// Whether <c>dotnet</c> on the <c>PATH</c> (or under <c>DOTNET_ROOT</c>) has an
    /// SDK, not just a runtime: only an SDK restores packages.
    /// </summary>
    public static bool DotNetSdkInstalled(IProcessEnvironment environment) => LatestSdk(environment) is not null;

    private static Version? LatestSdk(IProcessEnvironment environment)
    {
        var roots = new List<string>();
        if (environment.Get("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
            roots.Add(dotnetRoot);
        foreach (var directory in (environment.Get("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dotnet = new FileInfo(Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            if (!dotnet.Exists)
                continue;
            var target = dotnet.LinkTarget is null ? dotnet : dotnet.ResolveLinkTarget(returnFinalTarget: true) as FileInfo;
            if (target?.DirectoryName is { } root)
                roots.Add(root);
        }
        return roots
            .Select(root => Path.Combine(root, "sdk"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(directory => Version.TryParse(Path.GetFileName(directory).Split('-')[0], out var version) ? version : null)
            .Where(version => version is not null)
            .Max();
    }
}
