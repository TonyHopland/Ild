namespace ILD.Core.Services.Implementations.PackageFeeds;

/// <summary>
/// Whether NuGet in a run's processes will find the Azure Artifacts credential
/// provider, the only thing that turns <c>VSS_NUGET_EXTERNAL_FEED_ENDPOINTS</c>
/// into credentials. Without it a restore from a private feed fails with a 401
/// however good the PAT is, so it is looked for the way NuGet looks: the plugin
/// paths when set (they replace the default location), otherwise
/// <c>~/.nuget/plugins</c> in the home the processes run with.
/// </summary>
public static class NuGetCredentialProvider
{
    private const string PluginFile = "CredentialProvider.Microsoft.dll";

    /// <summary>The plugin a run process's NuGet would use, or null when it has none.</summary>
    public static string? Locate(IProcessEnvironment environment)
    {
        var configured = environment.Get("NUGET_NETCORE_PLUGIN_PATHS") ?? environment.Get("NUGET_PLUGIN_PATHS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(path => Path.GetFileName(path) == PluginFile && File.Exists(path));
        }

        var home = AgentIsolation.ResolveChildHome(
            environment.Get(AgentIsolation.AgentUserEnvVar), environment.Get(AgentIsolation.AgentHomeEnvVar))
            ?? environment.Get("HOME");
        if (string.IsNullOrWhiteSpace(home))
            return null;
        var path = Path.Combine(home, ".nuget", "plugins", "netcore", "CredentialProvider.Microsoft", PluginFile);
        return File.Exists(path) ? path : null;
    }

    public const string MissingWarning =
        "NuGet restores from the selected feeds will fail here: the Azure Artifacts credential provider is not installed.";
}
