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

    // -- Where a tool is installed ----------------------------------------------

    [Theory]
    [InlineData("node@20", true)]
    [InlineData("dotnet@10", true)]
    [InlineData("python@3.12", true)]
    [InlineData("npm:x", false)]
    [InlineData("cargo:x", false)]
    [InlineData("pipx:black", false)]
    [InlineData("aqua:cli/cli", false)]
    [InlineData("prettier", false)]
    [InlineData("pnpm@9", false)]
    [InlineData("node:evil@1", false)]
    public void Only_a_mise_core_tool_by_its_plain_name_is_installed_into_the_shared_toolchains(string spec, bool core)
        => Assert.Equal(core, RunTool.Parse(spec)!.IsCore);

    [Fact]
    public void A_plan_routes_core_tools_to_the_toolchains_and_every_other_to_the_repositorys_own_tools()
    {
        Write("mise.toml", """
            [tools]
            node = "22"
            "npm:x" = "1"
            "cargo:x" = "1"
            prettier = "3"
            """);

        var plan = RunToolDetector.Resolve(_repo);

        Assert.Equal(["node@22"], plan.Toolchains.Select(t => t.ToString()));
        Assert.Equal(["cargo:x@1", "npm:x@1", "prettier@3"], plan.RepositoryTools.Select(t => t.ToString()));
        Assert.Contains("node@22 from mise.toml -> toolchains", plan.ToString());
        Assert.Contains("npm:x@1 from mise.toml -> repository tools", plan.ToString());
    }

    [Fact]
    public void A_repository_can_opt_out_of_install_scripts_in_its_own_zone()
    {
        Write("mise.toml", "[tools]\n\"npm:x\" = \"1\"\n");
        Assert.False(RunToolDetector.Resolve(_repo).IgnoreScripts);

        Write("ild.config.json", """{ "run": { "ignoreScripts": true } }""");
        Assert.True(RunToolDetector.Resolve(_repo).IgnoreScripts);
    }

    [Fact]
    public void A_repositorys_folder_follows_what_it_names_and_not_the_order_or_another_repository()
    {
        var a = RunToolStore.RepositoryFolder([new RunTool("npm:x", "1"), new RunTool("cargo:y", "2")], false);

        Assert.Equal(a, RunToolStore.RepositoryFolder([new RunTool("cargo:y", "2"), new RunTool("npm:x", "1")], false));
        Assert.NotEqual(a, RunToolStore.RepositoryFolder([new RunTool("npm:x", "2"), new RunTool("cargo:y", "2")], false));
        Assert.NotEqual(a, RunToolStore.RepositoryFolder([new RunTool("npm:x", "1"), new RunTool("cargo:y", "2")], true));
        Assert.StartsWith(RunToolStore.RepositoryToolsRoot + "/", a);
    }

    [Fact]
    public void Install_scripts_are_off_for_the_installers_of_the_agents_and_toolchains_and_for_a_repository_that_opts_out()
    {
        Assert.Contains("npm_config_ignore_scripts=true", RunToolStore.InstallerEnvironment(RunToolStore.Root, true));
        Assert.Contains("npm_config_ignore_scripts=true", RunToolStore.InstallerEnvironment(RunToolStore.ToolchainsRoot, true));
        Assert.DoesNotContain("npm_config_ignore_scripts=true", RunToolStore.InstallerEnvironment("/opt/ild-repo-tools/abc", false));
        Assert.Contains("MISE_DATA_DIR=/opt/ild-repo-tools/abc/mise", RunToolStore.InstallerEnvironment("/opt/ild-repo-tools/abc", false));
        Assert.Contains("ILD_TOOLS_ROOT=/opt/ild-repo-tools/abc", RunToolStore.InstallerEnvironment("/opt/ild-repo-tools/abc", false));
    }

    [Fact]
    public void The_agents_own_npm_install_runs_the_scripts_it_approves_whatever_the_installers_default()
        => Assert.Contains("--ignore-scripts=false", RunToolStore.InstallScript);

    [Fact]
    public void A_zones_output_names_only_paths_under_that_zone()
    {
        var output = """{"kind":"tool","spec":"npm:x@1","version":"1","path":["/opt/ild-tools/agents/pi/1/wrap","/opt/ild-repo-tools/abc/mise/bin","/opt/ild-repo-tools/abcd/bin"],"env":{}}""";

        Assert.Equal(["/opt/ild-repo-tools/abc/mise/bin"], RunToolStore.Parse(output, "/opt/ild-repo-tools/abc").Path);
    }

    [Fact]
    public void The_zones_merge_with_the_core_toolchains_then_the_repositorys_then_the_agents_and_the_agents_node_last()
    {
        var agents = new RunToolEnvironment(["/opt/ild-tools/agents/pi/wrap"], new Dictionary<string, string>(), ["pi@1"], ["/opt/ild-tools/node/bin"], [new RunToolMount(RunToolZone.Agents)]);
        var toolchains = new RunToolEnvironment(["/opt/ild-toolchains/node/bin"], new Dictionary<string, string> { ["A"] = "t" }, ["node@24"], null, [new RunToolMount(RunToolZone.Toolchains)]);
        var repository = new RunToolEnvironment(["/opt/ild-repo-tools/h/x/bin"], new Dictionary<string, string> { ["A"] = "r", ["B"] = "r" }, ["npm:x@1"], null, [new RunToolMount(RunToolZone.Repository, Guid.Empty)]);

        var merged = RunToolEnvironment.Merge(agents, toolchains, repository);

        Assert.Equal(["/opt/ild-toolchains/node/bin", "/opt/ild-repo-tools/h/x/bin", "/opt/ild-tools/agents/pi/wrap"], merged.Path);
        Assert.Equal(["/opt/ild-tools/node/bin"], merged.Fallback);
        Assert.Equal(["node@24", "npm:x@1", "pi@1"], merged.Installed);
        Assert.Equal("t", merged.Variables["A"]);
        Assert.Equal("r", merged.Variables["B"]);
        Assert.Equal([RunToolZone.Toolchains, RunToolZone.Repository, RunToolZone.Agents], merged.Zones.Select(z => z.Zone));
        Assert.Empty(RunToolEnvironment.Merge(null, null, null).Zones);
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
    public void The_agents_node_goes_last_so_npx_works_in_every_run_and_a_projects_node_still_wins()
    {
        var output = """
            {"kind":"agentnode","spec":"node@24","version":"24.22.2","path":["/opt/ild-tools/mise/installs/node/24/bin"],"env":{}}
            {"kind":"tool","spec":"python@3.12","version":"3.12.16","path":["/opt/ild-tools/mise/installs/python/3.12/bin"],"env":{}}
            {"kind":"agent","key":"pi","version":"1.5.0","path":["/opt/ild-tools/agents/pi/1.5.0/wrap"]}
            """;

        var environment = RunToolStore.Parse(output);

        Assert.Equal(["/opt/ild-tools/mise/installs/node/24/bin"], environment.Fallback);
        Assert.Equal(["python@3.12.16", "pi@1.5.0"], environment.Installed);
        Assert.Equal(
            "PATH=/opt/ild-tools/mise/installs/python/3.12/bin:/opt/ild-tools/agents/pi/1.5.0/wrap:/h/.local/bin:"
            + $"/opt/ild-tools/mise/installs/node/24/bin:{RunToolEnvironment.ImagePath}",
            environment.For("/h")[0]);
    }

    [Fact]
    public void A_project_on_the_agents_node_version_has_it_once_and_first()
    {
        var environment = RunToolStore.Parse("""
            {"kind":"agentnode","spec":"node@24","version":"24.22.2","path":["/opt/ild-tools/mise/installs/node/24/bin"],"env":{}}
            {"kind":"tool","spec":"node@24","version":"24.22.2","path":["/opt/ild-tools/mise/installs/node/24/bin"],"env":{}}
            """);

        Assert.Empty(environment.Fallback!);
        Assert.StartsWith("PATH=/opt/ild-tools/mise/installs/node/24/bin:/h/.local/bin:", environment.For("/h")[0]);
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
