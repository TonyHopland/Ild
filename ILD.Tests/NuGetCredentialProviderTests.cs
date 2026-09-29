using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.PackageFeeds;

namespace ILD.Tests;

/// <summary>
/// Whether a run's NuGet will find the Azure Artifacts credential provider, looked
/// for the way NuGet looks, and only asked where a .NET SDK is installed at all.
/// </summary>
public sealed class NuGetCredentialProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ild-credprovider-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A <c>dotnet</c> on a PATH directory, with an SDK beside it when asked.</summary>
    private string DotNet(bool withSdk, string sdk = "10.0.100")
    {
        var dotnetRoot = Directory.CreateDirectory(Path.Combine(_root, withSdk ? $"dotnet-sdk-{sdk}" : "dotnet-runtime")).FullName;
        File.WriteAllText(Path.Combine(dotnetRoot, "dotnet"), "");
        Directory.CreateDirectory(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", "10.0.0"));
        if (withSdk)
            Directory.CreateDirectory(Path.Combine(dotnetRoot, "sdk", sdk));
        return dotnetRoot;
    }

    /// <summary>The .NET tool's command in a directory, runnable by everyone.</summary>
    private string Tool(string directory)
    {
        var path = Path.Combine(Directory.CreateDirectory(directory).FullName, NuGetCredentialProvider.ToolCommand);
        File.WriteAllText(path, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }

    private string Plugin(string directory, UnixFileMode? mode = null)
    {
        var path = Path.Combine(Directory.CreateDirectory(directory).FullName, "CredentialProvider.Microsoft.dll");
        File.WriteAllText(path, "");
        if (mode is { } m && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, m);
        return path;
    }

    private string Home() => Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;

    [Fact]
    public void A_provider_named_by_the_plugin_paths_is_found()
    {
        var plugin = Plugin(Path.Combine(_root, "system", "CredentialProvider.Microsoft"));
        var env = new TestProcessEnvironment { { "PATH", DotNet(withSdk: true) }, { "HOME", Home() }, { "NUGET_PLUGIN_PATHS", plugin } };

        Assert.Equal(plugin, NuGetCredentialProvider.Locate(env));
        Assert.False(NuGetCredentialProvider.IsMissing(env));
    }

    [Fact]
    public void A_provider_in_the_home_plugins_folder_is_found_when_no_plugin_paths_are_set()
    {
        var home = Home();
        var plugin = Plugin(Path.Combine(home, ".nuget", "plugins", "netcore", "CredentialProvider.Microsoft"));
        var env = new TestProcessEnvironment { { "PATH", DotNet(withSdk: true) }, { "HOME", home } };

        Assert.Equal(plugin, NuGetCredentialProvider.Locate(env));
        Assert.False(NuGetCredentialProvider.IsMissing(env));
    }

    [Fact]
    public void With_an_sdk_and_no_provider_it_is_missing()
    {
        var env = new TestProcessEnvironment { { "PATH", DotNet(withSdk: true) }, { "HOME", Home() } };

        Assert.True(NuGetCredentialProvider.IsMissing(env));
    }

    [Fact]
    public void Plugin_paths_that_name_no_provider_replace_the_home_folder_as_nuget_does()
    {
        var home = Home();
        Plugin(Path.Combine(home, ".nuget", "plugins", "netcore", "CredentialProvider.Microsoft"));
        var env = new TestProcessEnvironment
        {
            { "PATH", DotNet(withSdk: true) },
            { "HOME", home },
            { "NUGET_PLUGIN_PATHS", Path.Combine(_root, "gone", "CredentialProvider.Microsoft.dll") },
        };

        Assert.True(NuGetCredentialProvider.IsMissing(env));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_dotnet_sdk_nothing_is_missing(bool runtimeOnly)
    {
        var path = runtimeOnly ? DotNet(withSdk: false) : Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var env = new TestProcessEnvironment { { "PATH", path }, { "HOME", Home() } };

        Assert.False(NuGetCredentialProvider.DotNetSdkInstalled(env));
        Assert.False(NuGetCredentialProvider.IsMissing(env));
    }

    [Fact]
    public void Under_uid_isolation_a_provider_the_agent_cannot_read_does_not_count()
    {
        if (OperatingSystem.IsWindows()) return;
        var plugin = Plugin(Path.Combine(_root, "system", "CredentialProvider.Microsoft"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var env = new TestProcessEnvironment
        {
            { "PATH", DotNet(withSdk: true) },
            { "HOME", Home() },
            { "NUGET_PLUGIN_PATHS", plugin },
            { AgentIsolation.AgentUserEnvVar, "agent" },
        };

        Assert.True(NuGetCredentialProvider.IsMissing(env));
        File.SetUnixFileMode(plugin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.False(NuGetCredentialProvider.IsMissing(env));
    }

    [Fact]
    public void The_dotnet_tool_on_the_path_is_found_by_an_sdk_that_looks_there()
    {
        var tool = Tool(Path.Combine(_root, "usr-local-bin"));
        var env = new TestProcessEnvironment
        {
            { "PATH", string.Join(Path.PathSeparator, DotNet(withSdk: true), Path.GetDirectoryName(tool)) },
            { "HOME", Home() },
        };

        Assert.Equal(tool, NuGetCredentialProvider.Locate(env));
        Assert.False(NuGetCredentialProvider.IsMissing(env));
    }

    [Fact]
    public void An_sdk_before_9_0_200_does_not_look_on_the_path_so_the_tool_does_not_count()
    {
        var tool = Tool(Path.Combine(_root, "usr-local-bin"));
        var env = new TestProcessEnvironment
        {
            { "PATH", string.Join(Path.PathSeparator, DotNet(withSdk: true, sdk: "9.0.100"), Path.GetDirectoryName(tool)) },
            { "HOME", Home() },
        };

        Assert.True(NuGetCredentialProvider.IsMissing(env));
    }

    [Fact]
    public void Plugin_paths_that_name_no_provider_switch_the_path_lookup_off_as_nuget_does()
    {
        var tool = Tool(Path.Combine(_root, "usr-local-bin"));
        var env = new TestProcessEnvironment
        {
            { "PATH", string.Join(Path.PathSeparator, DotNet(withSdk: true), Path.GetDirectoryName(tool)) },
            { "HOME", Home() },
            { "NUGET_PLUGIN_PATHS", Path.Combine(_root, "gone", "CredentialProvider.Microsoft.dll") },
        };

        Assert.True(NuGetCredentialProvider.IsMissing(env));
        env.Set("NUGET_PLUGIN_PATHS", tool);
        Assert.Equal(tool, NuGetCredentialProvider.Locate(env));
    }
}
