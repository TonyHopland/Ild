namespace ILD.Tests;

/// <summary>Where the built ILD MCP server is, for the tests that load or launch it.</summary>
internal static class McpServerBinary
{
    public static string ServerDll => Path.Combine(AppContext.BaseDirectory, "ild-mcp-server.dll");

    /// <summary>
    /// The server as ILD.McpServer itself builds it. The copy in the test output
    /// cannot be launched with <c>dotnet</c> — its dependencies are resolved
    /// against the test project's deps file, so hosting assemblies are missing.
    /// </summary>
    public static string RunnableServerDll
    {
        get
        {
            var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = tfm.Parent!;
            var repoRoot = configuration.Parent!.Parent!.Parent!;
            var dll = Path.Combine(repoRoot.FullName, "ILD.McpServer", "bin", configuration.Name, tfm.Name, "ild-mcp-server.dll");
            Assert.True(File.Exists(dll), $"ILD.McpServer has not been built: {dll}");
            return dll;
        }
    }
}
