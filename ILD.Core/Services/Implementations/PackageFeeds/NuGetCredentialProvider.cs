namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// Whether NuGet in a run's processes will find the Azure Artifacts credential
/// provider, the only thing that turns <c>VSS_NUGET_EXTERNAL_FEED_ENDPOINTS</c>
/// into credentials. Without it a restore from a private feed fails with a 401
/// however good the PAT is, so it is looked for the way NuGet looks: the plugin
/// paths when set (they replace the default location), otherwise
/// <c>~/.nuget/plugins</c> in the home the processes run with. Where no .NET SDK
/// is installed nothing restores NuGet packages, so nothing is missing either.
/// </summary>
public static class NuGetCredentialProvider
{
    private const string PluginFile = "CredentialProvider.Microsoft.dll";

    public const string MissingWarning =
        "NuGet restores won't be authenticated: the Azure Artifacts credential provider was not found. npm feeds are unaffected.";

    /// <summary>True when a .NET SDK is installed and its NuGet will not find the credential provider.</summary>
    public static bool IsMissing(IProcessEnvironment environment)
        => DotNetSdkInstalled(environment) && Locate(environment) is null;

    /// <summary>The plugin a run process's NuGet would use, or null when it has none it can read.</summary>
    public static string? Locate(IProcessEnvironment environment)
    {
        var agentUser = environment.Get(AgentIsolation.AgentUserEnvVar);
        var configured = environment.Get("NUGET_NETCORE_PLUGIN_PATHS") ?? environment.Get("NUGET_PLUGIN_PATHS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(path => Path.GetFileName(path) == PluginFile && ReadableByRunProcesses(path, agentUser));
        }

        var home = AgentIsolation.ResolveChildHome(agentUser, environment.Get(AgentIsolation.AgentHomeEnvVar))
            ?? environment.Get("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;
        var path = Path.Combine(home, ".nuget", "plugins", "netcore", "CredentialProvider.Microsoft", PluginFile);
        return ReadableByRunProcesses(path, agentUser) ? path : null;
    }

    // Under uid isolation the run processes are another uid, and this process can
    // only vouch for a file that is readable by everyone, as the image's copy and
    // an installer's copy in the agent's home are.
    private static bool ReadableByRunProcesses(string path, string? agentUser)
    {
        if (!File.Exists(path))
            return false;
        if (string.IsNullOrWhiteSpace(agentUser) || OperatingSystem.IsWindows())
            return true;
        return File.GetUnixFileMode(path).HasFlag(UnixFileMode.OtherRead);
    }

    /// <summary>
    /// Whether <c>dotnet</c> on the <c>PATH</c> (or under <c>DOTNET_ROOT</c>) has an
    /// SDK, not just a runtime: only an SDK restores packages.
    /// </summary>
    public static bool DotNetSdkInstalled(IProcessEnvironment environment)
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
        return roots.Any(root => Directory.Exists(Path.Combine(root, "sdk"))
            && Directory.EnumerateDirectories(Path.Combine(root, "sdk")).Any());
    }
}
