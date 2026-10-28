using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace ILD.Tests;

/// <summary>
/// <c>dotnet ild-mcp-server.dll --launch &lt;file&gt;</c> as codex runs it for a
/// custom MCP server: the real <c>ild-mcp-server.dll</c> replaces itself with the
/// command the file names, so the server's secrets travel in the file rather
/// than in codex's argv or environment. A local <c>sh</c> stands in for the server.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class McpServerLauncherTests : IDisposable
{
    private const string ArgSecret = "arg-secret-value";
    private const string EnvSecret = "env-secret-value";

    private readonly string _dir = Directory.CreateTempSubdirectory("ild-mcp-launch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task The_launcher_becomes_the_server_in_place_with_the_files_environment_over_its_own_and_stdio_passed_through()
    {
        var file = Write(JsonSerializer.Serialize(new
        {
            command = "sh",
            args = new[]
            {
                "-c",
                "echo pid=$$; echo keep=$KEEP; echo override=$OVERRIDE; echo added=$ADDED; read line; echo got=$line; echo to-stderr >&2; exit 7",
                "server",
            },
            env = new Dictionary<string, string> { ["OVERRIDE"] = "from-file", ["ADDED"] = "from-file" },
        }));

        var run = await LaunchAsync(file, stdin: "hello\n", environment: new() { ["KEEP"] = "parent", ["OVERRIDE"] = "parent" });

        Assert.Equal(7, run.ExitCode);
        Assert.Equal(
            new[] { $"pid={run.Pid}", "keep=parent", "override=from-file", "added=from-file", "got=hello" },
            run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("to-stderr", run.Stderr.Trim());
    }

    public static TheoryData<string, int> Failures => new()
    {
        { "missing", 2 },
        { "{ not json", 2 },
        { "[1]", 2 },
        { $$"""{ "args": ["--token", "{{ArgSecret}}"], "env": { "K": "{{EnvSecret}}" } }""", 2 },
        { $$"""{ "command": "", "args": ["--token", "{{ArgSecret}}"], "env": { "K": "{{EnvSecret}}" } }""", 2 },
        { $$"""{ "command": "ild-no-such-mcp-server", "args": ["--token", "{{ArgSecret}}"], "env": { "K": "{{EnvSecret}}" } }""", 127 },
        { $$"""{ "command": "NOT_EXECUTABLE", "args": ["--token", "{{ArgSecret}}"], "env": { "K": "{{EnvSecret}}" } }""", 126 },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_launch_that_cannot_start_the_server_exits_with_one_line_naming_no_secret(string content, int exitCode)
    {
        var notExecutable = Path.Combine(_dir, "not-executable");
        File.WriteAllText(notExecutable, "#!/bin/sh\necho should-not-run\n");
        File.SetUnixFileMode(notExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var file = content == "missing"
            ? Path.Combine(_dir, "missing.json")
            : Write(content.Replace("NOT_EXECUTABLE", notExecutable));

        var run = await LaunchAsync(file, stdin: "", environment: new());

        Assert.Equal(exitCode, run.ExitCode);
        Assert.Empty(run.Stdout);
        var line = Assert.Single(run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        if (exitCode == 2)
            Assert.StartsWith("ild-mcp-launch: ", line);
        Assert.DoesNotContain(ArgSecret, run.Stderr);
        Assert.DoesNotContain(EnvSecret, run.Stderr);
    }

    private sealed record Run(int Pid, int ExitCode, string Stdout, string Stderr);

    private static async Task<Run> LaunchAsync(string file, string stdin, Dictionary<string, string> environment)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(McpServerBinary.RunnableServerDll);
        psi.ArgumentList.Add("--launch");
        psi.ArgumentList.Add(file);
        foreach (var name in psi.Environment.Keys.ToArray())
        {
            if (name.StartsWith("ILD_", StringComparison.Ordinal)
                || name.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(name);
        }
        foreach (var (name, value) in environment)
            psi.Environment[name] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = process.StandardOutput.ReadToEndAsync(guard.Token);
        var stderr = process.StandardError.ReadToEndAsync(guard.Token);
        try
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The launcher exited before reading stdin.
        }
        await process.WaitForExitAsync(guard.Token);
        return new Run(process.Id, process.ExitCode, await stdout, await stderr);
    }

    private string Write(string content)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }
}
