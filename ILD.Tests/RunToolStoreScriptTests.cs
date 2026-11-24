using System.Diagnostics;
using System.Text.Json.Nodes;
using ILD.Core.Services.Implementations.PackageFeeds;
using ILD.Core.Services.RunHosts.Local;
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
    private string EnvLog => Path.Combine(_dir, "env.log");

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
                       mkdir -p "$dir/bin"; echo "$spec" >> "$FAKE_LOG"
                       echo "mise $HTTPS_PROXY $https_proxy $ALL_PROXY $NODE_USE_ENV_PROXY" >> "$FAKE_ENV" ;;
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
            echo "npm $HTTPS_PROXY $https_proxy $ALL_PROXY $NODE_USE_ENV_PROXY" >> "$FAKE_ENV"
            """);
        // dotnet: the SDK version FAKE_SDK names; `tool install … --tool-path <dir>` leaves the provider's command there.
        WriteExecutable("dotnet", """
            #!/bin/bash
            case "$1" in
              --version) echo "${FAKE_SDK:-10.0.100}" ;;
              tool) dir=$(printf '%s\n' "$@" | grep -A1 -x -- --tool-path | tail -1)
                    ver=$(printf '%s\n' "$@" | grep -A1 -x -- --version | tail -1)
                    mkdir -p "$dir"; printf '#!/bin/sh\n' > "$dir/nuget-plugin-microsoft-artifacts-credential-provider"
                    chmod +x "$dir/nuget-plugin-microsoft-artifacts-credential-provider"; echo "credprovider $ver" >> "$FAKE_LOG" ;;
            esac
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

    // The script looks for musl in /lib, which a test cannot change; it runs a copy of the script that looks here instead.
    private string? _muslGlob;
    private string? _sdk;
    private IReadOnlyDictionary<string, string> _environment = new Dictionary<string, string>();

    private (int ExitCode, string StdOut, string StdErr) Run(params string[] arguments) => RunWithPath(null, arguments);

    private (int ExitCode, string StdOut, string StdErr) RunWithPath(string? onlyPath, params string[] arguments)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Has("jq") && Has("flock"), "needs Linux with bash, jq and flock");
        var psi = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", ScriptText(), "bash" }.Concat(arguments)) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = onlyPath ?? $"{Bin}:{Environment.GetEnvironmentVariable("PATH")}";
        psi.Environment["ILD_TOOLS_ROOT"] = Root;
        psi.Environment["MISE_DATA_DIR"] = Path.Combine(Root, "mise");
        psi.Environment["FAKE_LOG"] = Log;
        psi.Environment["FAKE_ENV"] = EnvLog;
        foreach (var (name, value) in _environment) psi.Environment[name] = value;
        if (_sdk is not null) psi.Environment["FAKE_SDK"] = _sdk;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    private string ScriptText()
    {
        var script = RunToolStore.InstallScript.Replace("\r\n", "\n");
        if (_muslGlob is null) return script;
        Assert.Contains("'/lib/*musl*'", script);
        return script.Replace("'/lib/*musl*'", $"'{_muslGlob}'");
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
    public void The_proxy_variables_of_the_installer_reach_mises_and_npms_environment()
    {
        _environment = AgentIsolation.EgressProxyEnvironment("http://installer:agents@ild.example:3128");

        var result = Run("agentnode=node@24", "agent=pi|@earendil-works/pi-coding-agent|1.5.0|pi");

        Assert.True(result.ExitCode == 0, result.StdErr);
        const string seen = "http://installer:agents@ild.example:3128 http://installer:agents@ild.example:3128 http://installer:agents@ild.example:3128 1";
        Assert.Equal([$"mise {seen}", $"npm {seen}"], File.ReadAllLines(EnvLog));
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
    // -- A release agent: the run host reads the manifest for its own platform --------------------------------------------

    private const string ReleaseUrl = "https://downloads.example/agy.tar.gz";

    private string ReleaseArchive(string entry, string content)
    {
        var source = Directory.CreateDirectory(Path.Combine(_dir, "release-src")).FullName;
        File.WriteAllText(Path.Combine(source, entry), content);
        var archive = Path.Combine(_dir, "release.tar.gz");
        using var tar = Process.Start(new ProcessStartInfo("tar", ["-czf", archive, "-C", source, entry]))!;
        tar.WaitForExit();
        return archive;
    }

    private string CurlLog => Path.Combine(_dir, "curl.log");

    /// <summary>A curl that answers the manifest and the archive from files, and logs each address it is asked for.</summary>
    private void ServeRelease(string archive, string version, string? sha512 = null, string url = ReleaseUrl)
    {
        var manifest = Path.Combine(_dir, "manifest.json");
        sha512 ??= Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(archive)));
        File.WriteAllText(manifest, $$"""{"version":"{{version}}","url":"{{url}}","sha512":"{{sha512}}"}""");
        WriteExecutable("curl", """
            #!/bin/bash
            out=""; url=""
            while [ $# -gt 0 ]; do case "$1" in -o) out="$2"; shift 2 ;; -*) shift ;; *) url="$1"; shift ;; esac; done
            echo "$url" >> "$FAKE_CURL_LOG"
            case "$url" in
              */manifests/*.json) cat "$FAKE_MANIFEST" ;;
              *agy.tar.gz) cp "$FAKE_ARCHIVE" "$out" ;;
              *) exit 22 ;;
            esac
            """);
        _environment = new Dictionary<string, string>(_environment)
        {
            ["FAKE_CURL_LOG"] = CurlLog, ["FAKE_MANIFEST"] = manifest, ["FAKE_ARCHIVE"] = archive,
        };
    }

    private static string Release(string version = "1.5.16")
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            key = "agy", manifest = "https://releases.example/manifests/{platform}.json", version, bin = "agy", entry = "antigravity",
        });

    private void Uname(string system, string machine)
    {
        WriteExecutable("uname", """
            #!/bin/bash
            case "$1" in -s) echo "$FAKE_SYSTEM" ;; -m) echo "$FAKE_MACHINE" ;; esac
            """);
        _environment = new Dictionary<string, string>(_environment) { ["FAKE_SYSTEM"] = system, ["FAKE_MACHINE"] = machine };
    }

    // The same table as ManagedAgentReleaseTests.Platforms_are_named_by_system_architecture_and_c_library, for the script.
    [Theory]
    [InlineData("Linux", "x86_64", false, "linux_amd64")]
    [InlineData("Linux", "aarch64", false, "linux_arm64")]
    [InlineData("Linux", "x86_64", true, "linux_amd64_musl")]
    [InlineData("Linux", "arm64", true, "linux_arm64_musl")]
    [InlineData("Darwin", "arm64", false, "darwin_arm64")]
    [InlineData("Darwin", "x86_64", true, "darwin_amd64")]
    public void A_run_host_names_its_platform_by_system_architecture_and_c_library(string system, string machine, bool musl, string expected)
    {
        ServeRelease(ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript), "1.5.16");
        Uname(system, machine);
        var libc = Directory.CreateDirectory(Path.Combine(_dir, "lib")).FullName;
        if (musl) File.WriteAllText(Path.Combine(libc, "ld-musl-x86_64.so.1"), "");
        _muslGlob = Path.Combine(libc, "*musl*");

        var result = Run("release=" + Release());

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal($"https://releases.example/manifests/{expected}.json", File.ReadAllLines(CurlLog)[0]);
    }

    [Theory]
    [InlineData("Linux", "riscv64")]
    [InlineData("FreeBSD", "x86_64")]
    public void A_run_host_with_no_build_for_it_says_so(string system, string machine)
    {
        ServeRelease(ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript), "1.5.16");
        Uname(system, machine);

        var result = Run("release=" + Release());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("has no build for", result.StdErr);
        Assert.False(File.Exists(CurlLog));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void The_script_unpacks_the_same_file_and_sets_the_same_mode_as_the_install_on_ilds_own_machine()
    {
        ServeRelease(ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript), "1.5.16");

        var result = Run("release=" + Release());

        Assert.True(result.ExitCode == 0, result.StdErr);
        var unpacked = Directory.EnumerateFiles(Root, "antigravity", SearchOption.AllDirectories).Single();
        Assert.Equal(ManagedAgentReleaseTests.AgentScript, File.ReadAllText(unpacked));
        Assert.Equal(ILD.Core.Services.Implementations.ManagedAgentService.ExecutableMode, File.GetUnixFileMode(unpacked));
    }

    [Fact]
    public void An_install_that_is_complete_is_used_without_asking_the_release_server_again()
    {
        ServeRelease(ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript), "1.5.16");
        Run("release=" + Release());
        File.Delete(CurlLog);
        WriteExecutable("curl", "#!/bin/bash\necho offline >&2\nexit 7");

        var result = Run("release=" + Release());

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal("1.5.16", JsonNode.Parse(result.StdOut.Trim())!["version"]!.GetValue<string>());
        Assert.DoesNotContain("offline", result.StdErr);
    }

    [Fact]
    public void With_the_server_unreachable_the_newest_install_on_the_host_is_used_with_a_warning()
    {
        ServeRelease(ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript), "1.5.16");
        Run("release=" + Release("1.5.16"));
        WriteExecutable("curl", "#!/bin/bash\nexit 7");

        var result = Run("release=" + Release("1.5.20"));

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal("1.5.16", JsonNode.Parse(result.StdOut.Trim())!["version"]!.GetValue<string>());
        Assert.Contains("could not read the agy release manifest", result.StdErr);
        Assert.Contains("the installed agy 1.5.16 is used", result.StdErr);
    }

    [Fact]
    public void A_manifest_the_server_offers_the_held_version_of_is_installed_at_that_version()
    {
        ServeRelease(ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript), "1.5.16");

        var result = Run("release=" + Release("1.5.16"));

        Assert.DoesNotContain("no longer offered", result.StdErr);
        Assert.Equal("1.5.16", JsonNode.Parse(result.StdOut.Trim())!["version"]!.GetValue<string>());
    }

    [Fact]
    public void An_archive_without_the_executable_stops_the_script_and_leaves_the_install_incomplete()
    {
        ServeRelease(ReleaseArchive("something-else", "x"), "1.5.16");

        var result = Run("release=" + Release());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("could not unpack antigravity", result.StdErr);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Root, "agents"), ".ild-complete", SearchOption.AllDirectories));
    }

    [Fact]
    public void An_executable_that_is_a_link_in_the_archive_is_not_taken()
    {
        var source = Directory.CreateDirectory(Path.Combine(_dir, "link-src")).FullName;
        File.CreateSymbolicLink(Path.Combine(source, "antigravity"), "/bin/sh");
        var archive = Path.Combine(_dir, "link.tar.gz");
        using (var tar = Process.Start(new ProcessStartInfo("tar", ["-czf", archive, "-C", source, "antigravity"]))!) tar.WaitForExit();
        ServeRelease(archive, "1.5.16");

        var result = Run("release=" + Release());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("has no regular file antigravity", result.StdErr);
    }

    [Fact]
    public void A_release_agent_is_read_from_the_manifest_of_the_run_hosts_own_platform_and_wrapped_without_the_agents_node()
    {
        ServeRelease(ReleaseArchive("antigravity", "#!/bin/sh\necho agy\n"), "1.5.16");

        var first = Run("release=" + Release());
        var second = Run("release=" + Release());

        Assert.True(first.ExitCode == 0, first.StdErr);
        Assert.Equal(first.StdOut, second.StdOut);
        var requested = File.ReadAllLines(CurlLog)[0];
        Assert.Matches(@"^https://releases\.example/manifests/(linux_(amd64|arm64)(_musl)?|darwin_(amd64|arm64))\.json$", requested);
        var platform = requested["https://releases.example/manifests/".Length..^".json".Length];
        var line = JsonNode.Parse(first.StdOut.Trim())!;
        Assert.Equal("agent", line["kind"]!.GetValue<string>());
        Assert.Equal("1.5.16", line["version"]!.GetValue<string>());
        var wrap = Path.Combine(Root, "agents", "agy", $"1.5.16-{platform}", "wrap");
        Assert.Equal([wrap], line["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Contains($"exec \"{Root}/agents/agy/1.5.16-{platform}/release/antigravity\"", File.ReadAllText(Path.Combine(wrap, "agy")));
        Assert.Single(File.ReadAllLines(CurlLog), url => url == ReleaseUrl);
        Assert.Empty(Installs());
    }

    [Fact]
    public void A_release_agent_whose_version_the_server_no_longer_offers_gets_the_one_it_names_and_says_so()
    {
        ServeRelease(ReleaseArchive("antigravity", "#!/bin/sh\n"), "1.6.0");

        var result = Run("release=" + Release("1.5.16"));

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal("1.6.0", JsonNode.Parse(result.StdOut.Trim())!["version"]!.GetValue<string>());
        Assert.Contains("1.5.16 is no longer offered", result.StdErr);
    }

    [Fact]
    public void A_release_that_does_not_match_its_manifests_checksum_is_not_installed_and_says_why()
    {
        ServeRelease(ReleaseArchive("antigravity", "#!/bin/sh\n"), "1.5.16", sha512: new string('0', 128));

        var result = Run("release=" + Release());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("", result.StdOut.Trim());
        Assert.Contains("does not match its manifest's checksum", result.StdErr);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Root, "agents", "agy"), ".ild-complete", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../../x")]
    [InlineData("1.5.16/../../x")]
    [InlineData("1.5.16 ")]
    [InlineData("latest")]
    [InlineData("")]
    public void A_manifest_version_that_is_not_a_version_number_is_refused_before_anything_is_written(string version)
    {
        ServeRelease(ReleaseArchive("antigravity", "#!/bin/sh\n"), version);

        var result = Run("release=" + Release());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("release manifest", result.StdErr);
        Assert.Contains("incomplete", result.StdErr);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Root, "agents")));
        Assert.False(File.Exists(Path.Combine(_dir, "x")));
    }

    [Theory]
    [InlineData("../../x", ReleaseUrl, null)]
    [InlineData("1.6.0", "http://downloads.example/agy.tar.gz", null)]
    [InlineData("1.6.0", ReleaseUrl, "")]
    public void A_manifest_that_is_not_usable_falls_back_to_the_install_on_the_host_like_one_that_cannot_be_read(string version, string url, string? sha512)
    {
        var archive = ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript);
        ServeRelease(archive, "1.5.16");
        Run("release=" + Release("1.5.16"));
        ServeRelease(archive, version, sha512, url);

        var result = Run("release=" + Release("1.5.20"));

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal("1.5.16", JsonNode.Parse(result.StdOut.Trim())!["version"]!.GetValue<string>());
        Assert.Contains("release manifest", result.StdErr);
        Assert.Contains("is incomplete; the installed agy 1.5.16 is used", result.StdErr);
        Assert.Single(Directory.GetDirectories(Path.Combine(Root, "agents", "agy")));
        Assert.False(File.Exists(Path.Combine(_dir, "x")));
        Assert.False(File.Exists(Path.Combine(Root, "x")));
    }

    [Fact]
    public void A_manifest_that_is_not_json_falls_back_to_the_install_or_says_there_is_none()
    {
        var archive = ReleaseArchive("antigravity", ManagedAgentReleaseTests.AgentScript);
        ServeRelease(archive, "1.5.16");
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), "<html>not found</html>");

        var none = Run("release=" + Release("1.5.20"));
        Assert.NotEqual(0, none.ExitCode);
        Assert.Contains("is incomplete, and no agy is installed here", none.StdErr);

        File.WriteAllText(Path.Combine(_dir, "manifest.json"), $$"""{"version":"1.5.16","url":"{{ReleaseUrl}}","sha512":"{{Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(File.ReadAllBytes(archive)))}}"}""");
        Run("release=" + Release("1.5.16"));
        File.WriteAllText(Path.Combine(_dir, "manifest.json"), "<html>not found</html>");

        var installed = Run("release=" + Release("1.5.20"));
        Assert.True(installed.ExitCode == 0, installed.StdErr);
        Assert.Equal("1.5.16", JsonNode.Parse(installed.StdOut.Trim())!["version"]!.GetValue<string>());
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_stops_the_script_with_a_clear_message()
    {
        WriteExecutable("curl", "#!/bin/bash\nexit 22");

        var result = Run("release=" + Release());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("could not read the agy release manifest", result.StdErr);
        Assert.Contains("no agy is installed here", result.StdErr);
    }

    [Fact]
    public void A_run_host_without_curl_says_so_instead_of_failing_on_the_first_command_that_needs_it()
    {
        var tools = Directory.CreateDirectory(Path.Combine(_dir, "tools")).FullName;
        foreach (var tool in new[] { "bash", "jq", "flock", "tar", "sha512sum", "uname", "mkdir", "rm", "chmod", "touch", "head", "tr", "readlink", "cat", "grep", "sed", "env", "dirname", "ls" })
        {
            var source = new[] { "/usr/bin/" + tool, "/bin/" + tool }.FirstOrDefault(File.Exists);
            if (source is not null) File.CreateSymbolicLink(Path.Combine(tools, tool), source);
        }

        var result = RunWithPath(tools, "release=" + Release());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("curl is needed to install agy", result.StdErr);
    }

    private static string[] PathOf(string stdout) => JsonNode.Parse(stdout.Trim())!["path"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();

    [Fact]
    public void A_dotnet_sdk_from_9_0_200_gets_the_azure_artifacts_credential_provider_on_its_path_once_per_sdk()
    {
        var first = Run("tool=dotnet@10");
        var again = Run("tool=dotnet@10");
        var sameSdk = Run("tool=dotnet@10.0");

        Assert.True(first.ExitCode == 0, first.StdErr);
        var provider = $"{Root}/nuget-credprovider/2.0.6/10.0.100";
        Assert.Equal([$"{Root}/mise/installs/dotnet/10.0/bin", provider], PathOf(first.StdOut));
        Assert.True(File.Exists(Path.Combine(provider, NuGetCredentialProvider.ToolCommand)));
        Assert.Equal(first.StdOut, again.StdOut);
        Assert.Contains(provider, PathOf(sameSdk.StdOut));
        Assert.Equal(["dotnet@10", "credprovider 2.0.6", "dotnet@10.0"], Installs());
    }

    [Fact]
    public void An_sdk_older_than_9_0_200_gets_no_provider_and_says_why()
    {
        _sdk = "8.0.404";
        var result = Run("tool=dotnet@8");

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Equal([$"{Root}/mise/installs/dotnet/8.0/bin"], PathOf(result.StdOut));
        Assert.Contains("only from SDK 9.0.200", result.StdErr);
        Assert.Equal(["dotnet@8"], Installs());
        Assert.Equal(result.StdOut, Run("tool=dotnet@8").StdOut);
        Assert.Equal(["dotnet@8"], Installs());
    }

    [Fact]
    public void A_dotnet_manifest_from_before_the_provider_gets_it_without_installing_dotnet_again()
    {
        Run("tool=dotnet@10");
        var manifest = Path.Combine(Root, "manifests", "dotnet@10.json");
        var old = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
        old.Remove("nuget");
        old["path"] = new JsonArray(old["path"]!.AsArray()[0]!.GetValue<string>());
        File.WriteAllText(manifest, old.ToJsonString());

        var result = Run("tool=dotnet@10");

        Assert.True(result.ExitCode == 0, result.StdErr);
        Assert.Contains($"{Root}/nuget-credprovider/2.0.6/10.0.100", PathOf(result.StdOut));
        Assert.Equal(["dotnet@10", "credprovider 2.0.6"], Installs());
    }
}
