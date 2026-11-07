using System.Diagnostics;
using System.Text.Json.Nodes;
using ILD.Core.Services.RunHosts.Tools;

namespace ILD.Tests;

/// <summary>
/// The store's install script itself, run by bash against a fake mise and npm, so what it does with their answers,
/// and with a failure, is checked without downloading anything. Needs bash, jq and flock, as the runner image has.
/// </summary>
public sealed class RunToolStoreScriptTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ild-tool-script-").FullName;
    private string Root => Path.Combine(_dir, "store");
    private string Bin => Path.Combine(_dir, "bin");
    private string Log => Path.Combine(_dir, "installs.log");

    public RunToolStoreScriptTests()
    {
        Directory.CreateDirectory(Bin);
        // mise: node@99 does not exist; any other version installs as <version>.0.
        WriteExecutable("mise", """
            #!/bin/bash
            cmd=$1; shift
            [ "$cmd" = env ] && shift
            spec=$1; name=${spec%@*}; ver=${spec#*@}; dir="$MISE_DATA_DIR/installs/$name/$ver.0"
            case "$cmd" in
              install) [ "$spec" = node@99 ] && { echo "mise ERROR 404 Not Found for $spec" >&2; exit 1; }
                       mkdir -p "$dir/bin"; echo "$spec" >> "$FAKE_LOG" ;;
              where) echo "$dir" ;;
              env) printf '{"PATH":"%s/bin:%s","FOO_HOME":"%s","MISE_SHELL":"bash"}' "$dir" "$PATH" "$dir" ;;
            esac
            """);
        // npm install <pkg>@<version> --prefix <dir>: a node CLI named pi.
        WriteExecutable("npm", """
            #!/bin/bash
            prefix=$(printf '%s\n' "$@" | grep -A1 -x -- --prefix | tail -1)
            mkdir -p "$prefix/node_modules/pkg" "$prefix/node_modules/.bin"
            printf '#!/usr/bin/env node\n' > "$prefix/node_modules/pkg/cli.js"
            ln -sf ../pkg/cli.js "$prefix/node_modules/.bin/pi"
            echo "npm $2" >> "$FAKE_LOG"
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteExecutable(string name, string content)
    {
        var path = Path.Combine(Bin, name);
        File.WriteAllText(path, content.Replace("\r\n", "\n") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool Has(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("bash", ["-c", $"command -v {tool}"]) { RedirectStandardOutput = true })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private (int ExitCode, string StdOut, string StdErr) Run(params string[] arguments)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Has("jq") && Has("flock"), "needs Linux with bash, jq and flock");
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", RunToolStore.InstallScript.Replace("\r\n", "\n"), "bash" }.Concat(arguments)) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = $"{Bin}:{Environment.GetEnvironmentVariable("PATH")}";
        psi.Environment["ILD_TOOLS_ROOT"] = Root;
        psi.Environment["MISE_DATA_DIR"] = Path.Combine(Root, "mise");
        psi.Environment["FAKE_LOG"] = Log;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private string[] Installs() => File.Exists(Log) ? File.ReadAllLines(Log) : [];

    [Fact]
    public void A_toolchain_is_installed_once_and_its_manifest_names_its_path_and_variables()
    {
        var first = Run("tool=node@20");
        var second = Run("tool=node@20");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.StdOut, second.StdOut);
        Assert.Equal(["node@20"], Installs());
        var line = JsonNode.Parse(first.StdOut.Trim())!;
        Assert.Equal("tool", line["kind"]!.GetValue<string>());
        Assert.Equal("20.0", line["version"]!.GetValue<string>());
        Assert.Equal([$"{Root}/mise/installs/node/20.0/bin"], line["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal($"{Root}/mise/installs/node/20.0", line["env"]!["FOO_HOME"]!.GetValue<string>());
        Assert.Null(line["env"]!["MISE_SHELL"]);
    }

    [Fact]
    public void A_toolchain_that_cannot_be_installed_stops_the_script_and_leaves_no_manifest()
    {
        var result = Run("tool=node@99", "tool=python@3.12");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("404 Not Found for node@99", result.StdErr);
        Assert.Equal("", result.StdOut.Trim());
        Assert.DoesNotContain(Installs(), i => i.StartsWith("python", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Root, "manifests", "node@99.json")) && new FileInfo(Path.Combine(Root, "manifests", "node@99.json")).Length > 0);
    }

    [Fact]
    public void An_agent_is_installed_once_at_its_version_with_a_wrapper_that_starts_it_on_the_agents_node()
    {
        var first = Run("agentnode=node@24", "agent=pi|@earendil-works/pi-coding-agent|1.5.0|pi");
        var second = Run("agentnode=node@24", "agent=pi|@earendil-works/pi-coding-agent|1.5.0|pi");

        Assert.True(first.ExitCode == 0, first.StdErr);
        Assert.Equal(first.StdOut, second.StdOut);
        Assert.Equal(["node@24", "npm @earendil-works/pi-coding-agent@1.5.0"], Installs());
        var lines = first.StdOut.Trim().Split('\n').Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(["agentnode", "agent"], lines.Select(l => l["kind"]!.GetValue<string>()));
        Assert.Equal([$"{Root}/mise/installs/node/24.0/bin"], lines[0]["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        var line = lines[1];
        var wrap = Path.Combine(Root, "agents", "pi", "1.5.0", "wrap");
        Assert.Equal([wrap], line["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        var wrapper = File.ReadAllText(Path.Combine(wrap, "pi"));
        Assert.Contains($"exec \"{Root}/mise/installs/node/24.0/bin/node\" \"{Root}/agents/pi/1.5.0/node_modules/pkg/cli.js\"", wrapper);
    }
}
