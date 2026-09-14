using System.Reflection;

namespace ILD.Tests;

/// <summary>
/// The ILD MCP server's tool names, read straight off its
/// <c>[McpServerTool(Name = …)]</c> attributes — the one tool definition every
/// adapter is checked against.
/// </summary>
internal static class McpServerToolReflection
{
    public static IReadOnlyList<string> Names()
        => typeof(ILD.McpServer.Tools.LoopTools).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .SelectMany(m => m.CustomAttributes)
            .Where(a => a.AttributeType.Name == "McpServerToolAttribute")
            .Select(a => (string)a.NamedArguments.Single(n => n.MemberName == "Name").TypedValue.Value!)
            .ToArray();

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
