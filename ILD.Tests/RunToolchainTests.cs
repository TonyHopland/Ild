using ILD.Core.Services.Implementations.Adapters;
using ILD.Core.Services.RunHosts.Tools;

namespace ILD.Tests;

public sealed class RunToolchainTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("ild-run-tools-").FullName;

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    private void Write(string path, string content = "")
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private IReadOnlyList<string> Detected() => RunToolDetector.Resolve(_repo).Tools.Select(t => t.ToString()).ToList();

    // -- Detection ---------------------------------------------------------------

    [Fact]
    public void A_repository_with_no_marker_needs_no_toolchain()
    {
        Write("README.md", "hi");
        Assert.Empty(Detected());
    }

    [Fact]
    public void A_node_project_gets_node_at_its_nvmrc_or_the_default_lts()
    {
        Write("package.json", "{}");
        Assert.Equal(["node@24"], Detected());

        Write(".nvmrc", "v20.11.1\n");
        Assert.Equal(["node@20.11.1"], Detected());

        Write(".nvmrc", "lts/iron");
        Assert.Equal(["node@lts"], Detected());
    }

    [Fact]
    public void A_node_project_does_not_get_dotnet()
    {
        Write("package.json", "{}");
        Write("frontend/package.json", "{}");
        Assert.DoesNotContain(Detected(), t => t.StartsWith("dotnet", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dotnet_solution_gets_the_newest_target_framework_major()
    {
        Write("App.sln");
        Write("App/App.csproj", "<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>");
        Write("Lib/Lib.csproj", "<Project><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework></PropertyGroup></Project>");
        Assert.Equal(["dotnet@10"], Detected());
    }

    [Fact]
    public void A_project_two_folders_down_is_found_and_three_down_is_not()
    {
        Write("src/App/App.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("a/b/c/go.mod", "go 1.22");
        Assert.Equal(["dotnet@10"], Detected());
    }

    [Fact]
    public void Global_json_pins_the_dotnet_sdk()
    {
        Write("App.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("global.json", """{ "sdk": { "version": "9.0.300", "rollForward": "latestFeature" } }""");
        Assert.Equal(["dotnet@9.0.300"], Detected());
    }

    [Fact]
    public void A_mixed_repository_gets_each_of_its_toolchains()
    {
        Write("package.json", "{}");
        Write("api/Api.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("tools/pyproject.toml");
        Write(".python-version", "3.12\n");
        Write("svc/go.mod", "module x\n\ngo 1.22\ntoolchain go1.23.4\n");
        Assert.Equal(["dotnet@10", "go@1.23.4", "node@24", "python@3.12"], Detected());
    }

    [Fact]
    public void Markers_inside_dependencies_and_hidden_folders_are_not_looked_at()
    {
        Write("node_modules/x/go.mod", "go 1.22");
        Write(".github/package.json", "{}");
        Assert.Empty(Detected());
    }

    [Fact]
    public void A_project_that_pins_its_tools_for_mise_gets_those()
    {
        Write("package.json", "{}");
        Write("mise.toml", """
            [env]
            NODE_ENV = "development"

            [tools]
            node = "22"   # the LTS
            python = ["3.12", "3.11"]
            go = { version = "1.23" }
            "npm:prettier" = "3"
            """);
        Assert.Equal(["go@1.23", "node@22", "npm:prettier@3", "python@3.12"], Detected());
    }

    [Fact]
    public void Tool_versions_is_read_like_mise_reads_it()
    {
        Write(".tool-versions", "nodejs-ignored\nnode 20.10.0 18\n# comment\nruby 3.3.0\n");
        Assert.Equal(["node@20.10.0", "ruby@3.3.0"], Detected());
    }

    [Fact]
    public void The_config_names_tools_and_wins_over_what_was_found()
    {
        Write("package.json", "{}");
        Write("ild.config.json", """
            {
              // the frontend needs the newer one
              "run": { "tools": ["node@22", "pnpm@9"] },
            }
            """);
        Assert.Equal(["node@22", "pnpm@9"], Detected());
    }

    [Fact]
    public void The_config_can_name_tools_as_a_map_and_turn_finding_off()
    {
        Write("package.json", "{}");
        Write("ild.config.json", """{ "run": { "detect": false, "tools": { "python": "3.12" } } }""");
        Assert.Equal(["python@3.12"], Detected());
    }

    [Fact]
    public void A_config_without_a_run_section_or_one_that_is_not_json_changes_nothing()
    {
        Write("package.json", "{}");
        Write("ild.config.json", """{ "preview": {} }""");
        Assert.Equal(["node@24"], Detected());

        Write("ild.config.json", "{ not json");
        Assert.Equal(["node@24"], Detected());
    }

    [Theory]
    [InlineData("node@24; rm -rf /")]
    [InlineData("node@$(id)")]
    [InlineData("node@24 --force")]
    [InlineData("../node@24")]
    [InlineData("Node@`x`")]
    public void A_tool_that_could_carry_a_shell_character_is_refused(string spec)
        => Assert.Null(RunTool.Parse(spec));

    [Fact]
    public void A_bare_tool_name_means_its_newest_release()
        => Assert.Equal(new RunTool("bun", "latest"), RunTool.Parse("bun"));

    [Fact]
    public void A_refused_entry_in_the_config_is_left_out_and_the_rest_kept()
    {
        Write("ild.config.json", """{ "run": { "detect": false, "tools": ["node@24", "evil@$(id)"] } }""");
        Assert.Equal(["node@24"], Detected());
    }

    // -- The store ---------------------------------------------------------------

    [Fact]
    public void The_installer_is_asked_for_the_agents_node_then_the_toolchains_then_the_agents()
    {
        var arguments = RunToolStore.Arguments([new RunTool("dotnet", "10")], [new RunAgent(ManagedAgentCatalog.Codex, "0.167.0")]);
        Assert.Equal(["agentnode=node@24", "tool=dotnet@10", "agent=codex|@openai/codex|0.167.0|codex"], arguments);
        Assert.Empty(RunToolStore.Arguments([], []));
        Assert.Equal(["tool=go@1.23"], RunToolStore.Arguments([new RunTool("go", "1.23")], []));
    }

    [Fact]
    public void The_installers_output_becomes_the_runs_path_and_variables()
    {
        var output = """
            mise node@20.19.1 ✓ installed
            {"kind":"tool","spec":"node@20","version":"20.19.1","path":["/opt/ild-tools/mise/installs/node/20.19.1/bin"],"env":{}}
            {"kind":"tool","spec":"dotnet@10","version":"10.0.100","path":["/opt/ild-tools/mise/installs/dotnet/10.0.100"],"env":{"DOTNET_ROOT":"/opt/ild-tools/mise/installs/dotnet/10.0.100"}}
            {"kind":"agent","key":"codex","version":"0.167.0","path":["/opt/ild-tools/agents/codex/0.167.0/wrap"]}
            """;

        var environment = RunToolStore.Parse(output);

        Assert.Equal(
            ["/opt/ild-tools/mise/installs/node/20.19.1/bin", "/opt/ild-tools/mise/installs/dotnet/10.0.100", "/opt/ild-tools/agents/codex/0.167.0/wrap"],
            environment.Path);
        Assert.Equal("/opt/ild-tools/mise/installs/dotnet/10.0.100", environment.Variables["DOTNET_ROOT"]);
        Assert.Equal(["node@20.19.1", "dotnet@10.0.100", "codex@0.167.0"], environment.Installed);
    }

    [Fact]
    public void A_path_outside_the_store_is_never_put_on_a_runs_path()
    {
        var environment = RunToolStore.Parse("""{"kind":"tool","spec":"node@20","version":"20","path":["/usr/evil/bin","/opt/ild-tools/mise/installs/node/20/bin"],"env":{}}""");
        Assert.Equal(["/opt/ild-tools/mise/installs/node/20/bin"], environment.Path);
    }

    [Fact]
    public void A_runs_environment_puts_toolchains_then_agents_then_its_home_then_the_image_on_path()
    {
        var environment = new RunToolEnvironment(["/opt/ild-tools/a/bin", "/opt/ild-tools/agents/x/wrap"], new Dictionary<string, string> { ["DOTNET_ROOT"] = "/opt/ild-tools/d" }, []);

        var assignments = environment.For("/ild-run/r/home");

        Assert.Equal(
            [
                $"PATH=/opt/ild-tools/a/bin:/opt/ild-tools/agents/x/wrap:/ild-run/r/home/.local/bin:{RunToolEnvironment.ImagePath}",
                "NPM_CONFIG_PREFIX=/ild-run/r/home/.local",
                "DOTNET_ROOT=/opt/ild-tools/d",
            ],
            assignments);
    }

    // -- Agents ------------------------------------------------------------------

    [Fact]
    public void A_run_gets_the_agents_ild_has_installed_at_the_version_ild_selected()
    {
        var dataRoot = Path.Combine(_repo, "data");
        InstallAgent(dataRoot, ManagedAgentCatalog.Codex, "v1", "0.160.0");
        InstallAgent(dataRoot, ManagedAgentCatalog.Codex, "v2", "0.167.0");
        File.WriteAllText(Path.Combine(ManagedAgentInstall.AgentRoot(dataRoot, ManagedAgentCatalog.Codex), "current"), "v2");
        InstallAgent(dataRoot, ManagedAgentCatalog.Pi, "p1", "1.5.0");

        var agents = RunAgent.Installed(dataRoot);

        Assert.Equal([new RunAgent(ManagedAgentCatalog.Codex, "0.167.0")], agents);
    }

    private static void InstallAgent(string dataRoot, ManagedAgent agent, string id, string version)
    {
        var package = Path.Combine(ManagedAgentInstall.VersionDir(dataRoot, agent, id), "node_modules", agent.NpmPackage);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), $$"""{ "name": "{{agent.NpmPackage}}", "version": "{{version}}" }""");
    }
}
