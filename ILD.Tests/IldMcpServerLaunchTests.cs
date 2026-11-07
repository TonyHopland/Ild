using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

public sealed class IldMcpServerLaunchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-mcp-launch-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_server_with_an_apphost_starts_through_it_so_a_run_needs_no_dotnet_on_its_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the apphost has an .exe there and dotnet is always used");
        var dll = Path.Combine(_dir, "ild-mcp-server.dll");
        File.WriteAllText(dll, "");
        File.WriteAllText(Path.Combine(_dir, "ild-mcp-server"), "");

        var (command, arguments) = IldMcpServer.LaunchFor(dll);

        Assert.Equal(Path.Combine(_dir, "ild-mcp-server"), command);
        Assert.Empty(arguments);
    }

    [Fact]
    public void A_server_with_only_its_dll_starts_through_dotnet()
    {
        var dll = Path.Combine(_dir, "ild-mcp-server.dll");
        File.WriteAllText(dll, "");

        var (command, arguments) = IldMcpServer.LaunchFor(dll);

        Assert.Equal("dotnet", command);
        Assert.Equal([dll], arguments);
    }
}
