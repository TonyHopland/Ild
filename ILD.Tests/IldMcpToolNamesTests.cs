using ILD.Core.Services.Implementations.Adapters;

namespace ILD.Tests;

/// <summary>
/// Pi's <c>--tools</c> allowlist also filters extension tools, so the adapter has
/// to name every ILD MCP tool up front. It reads them from the server DLL's
/// metadata rather than keeping a list that can drift.
/// </summary>
public class IldMcpToolNamesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ild-mcp-names-" + Guid.NewGuid().ToString("N"));

    public IldMcpToolNamesTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Read_returns_every_McpServerTool_name_in_the_server_dll()
    {
        var names = IldMcpToolNames.Read(McpServerToolReflection.ServerDll);

        Assert.Contains("get_workitem", names);
        Assert.Equal(
            McpServerToolReflection.Names().OrderBy(n => n, StringComparer.Ordinal),
            names.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Read_returns_empty_for_a_missing_file()
    {
        Assert.Empty(IldMcpToolNames.Read(Path.Combine(_tempDir, "nope.dll")));
    }

    [Fact]
    public void Read_returns_empty_for_a_file_that_is_not_an_assembly()
    {
        var junk = Path.Combine(_tempDir, "ild-mcp-server.dll");
        File.WriteAllText(junk, "not a PE image");

        Assert.Empty(IldMcpToolNames.Read(junk));
    }

    [Fact]
    public void Read_rereads_a_dll_replaced_at_the_same_path()
    {
        var dll = Path.Combine(_tempDir, "ild-mcp-server.dll");
        File.Copy(McpServerToolReflection.ServerDll, dll);
        Assert.NotEmpty(IldMcpToolNames.Read(dll));

        File.WriteAllText(dll, "not a PE image");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(5));

        Assert.Empty(IldMcpToolNames.Read(dll));
    }
}
