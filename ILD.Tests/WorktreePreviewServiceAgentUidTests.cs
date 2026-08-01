using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Proves a Worktree Preview runs as the agent uid, and that everything it needs
/// to write follows it there (ADR-0016).
///
/// <para>
/// Isolation is driven through the explicit-parameter constructor rather than the
/// process-global <c>ILD_AGENT_USER</c>, which would turn it on for every other
/// test in the host. The assertions are made against the constructed
/// <c>ProcessStartInfo</c> rather than a spawned process, because a real crossing
/// execs <c>setpriv --reuid ... --init-groups</c>, which needs <c>CAP_SETUID</c>
/// and <c>CAP_SETGID</c> and a second uid — this host has neither (verified: it
/// fails with "initgroups failed"). Inspecting the psi is also the stronger check,
/// since it sees the absolute <c>setpriv</c> path that a PATH-based interception
/// could not.
/// </para>
/// </summary>
[Collection("EnvironmentPath")]
public class WorktreePreviewServiceAgentUidTests : IDisposable
{
    private const string AgentUser = "agent";
    private const string AgentGroup = "ild-agents";

    private readonly string _agentHome;
    private readonly string _stateDirectory;
    private readonly string? _originalHome;
    private readonly string? _originalPath;
    private string? _redirectedHome;

    public WorktreePreviewServiceAgentUidTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _agentHome = Path.Combine(Path.GetTempPath(), "ild-agentuid-home-" + id);
        _stateDirectory = Path.Combine(Path.GetTempPath(), "ild-agentuid-state-" + id);
        Directory.CreateDirectory(_agentHome);
        Directory.CreateDirectory(_stateDirectory);

        // BuildDefaultEnvironment reads (and EnsureInstalledToolsOnProcessPath
        // mutates) the process HOME/PATH; restore both so no other test inherits it.
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _originalPath = Environment.GetEnvironmentVariable("PATH");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        foreach (var directory in new[] { _agentHome, _stateDirectory, _redirectedHome })
        {
            if (directory is null) continue;
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private WorktreePreviewService BuildService(string? agentUser = AgentUser, bool withAgentHome = true)
    {
        var factory = new Mock<IHttpClientFactory>();
        var configuration = new ConfigurationBuilder().Build();
        return new WorktreePreviewService(factory.Object, configuration, PreviewProxyBase.Disabled,
            NullLogger<WorktreePreviewService>.Instance,
            agentUser, AgentGroup, withAgentHome ? _agentHome : null);
    }

    private WorktreePreviewService.ResolvedStep Step() => new(
        "npm run dev",
        _stateDirectory,
        new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Point the orchestrator's own <c>HOME</c> at an empty directory for one test.
    /// Process-global, and safe only inside the serialized <c>EnvironmentPath</c>
    /// collection.
    /// </summary>
    private string RedirectOrchestratorHome()
    {
        _redirectedHome = Path.Combine(Path.GetTempPath(), "ild-agentuid-orchhome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_redirectedHome);
        Environment.SetEnvironmentVariable("HOME", _redirectedHome);
        return _redirectedHome;
    }

    [Fact]
    public void Preview_commands_cross_to_the_agent_uid()
    {
        var psi = BuildService().BuildPreviewProcess(Step());

        // --reuid is the whole difference from the capability-only wrap the preview
        // used to get: the preview and the agent's own builds in the same worktree
        // are now one uid, which is what removes the MSB3021/MSB3374 class of
        // failure on files the other side owns.
        Assert.Equal("/usr/bin/setpriv", psi.FileName);
        Assert.Contains("--reuid=" + AgentUser, psi.ArgumentList);
        Assert.Contains("--regid=" + AgentGroup, psi.ArgumentList);
        Assert.Contains("--init-groups", psi.ArgumentList);
        Assert.Contains("--inh-caps=-all", psi.ArgumentList);
        Assert.Contains("--ambient-caps=-all", psi.ArgumentList);

        // The real command still follows the terminator, unaltered.
        var terminator = psi.ArgumentList.IndexOf("--");
        Assert.Equal(new[] { "/bin/sh", "-lc", "npm run dev" }, psi.ArgumentList.Skip(terminator + 1));
    }

    [Fact]
    public void The_privilege_drop_tool_is_never_resolved_through_PATH()
    {
        // setpriv is what performs the drop, so whoever controls its resolution
        // controls whether the drop happens. .NET resolves a bare FileName against
        // the child environment's PATH, and a preview child's PATH includes an
        // agent-writable npm bin directory — a planted `setpriv` there would be
        // exec'd as the orchestrator with the ambient capabilities still held,
        // because nothing ever dropped them. An absolute path removes the lookup.
        var psi = BuildService().BuildPreviewProcess(Step());

        Assert.True(Path.IsPathRooted(psi.FileName),
            $"the privilege-drop tool must be an absolute path, was '{psi.FileName}'");
    }

    [Fact]
    public void Nothing_is_routed_when_isolation_is_off()
    {
        // The documented escape hatch: ILD_AGENT_USER unset means the command runs
        // inline as the current user, exactly as before uid isolation existed.
        var psi = BuildService(agentUser: null).BuildPreviewProcess(Step());

        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-lc", "npm run dev" }, psi.ArgumentList);
    }

    [Fact]
    public void The_npm_prefix_follows_the_agent_home()
    {
        // $HOME/.local is where `npm install -g` puts a global CLI, and the agent
        // uid is both what runs the install and what execs the result afterwards.
        // Pointing it at the orchestrator's home instead leaves the install writing
        // into a 0710 directory it cannot enter.
        var environment = BuildService().BuildDefaultEnvironment(_stateDirectory);

        Assert.Equal(_agentHome, environment["HOME"]);
        Assert.Equal(Path.Combine(_agentHome, ".local"), environment["NPM_CONFIG_PREFIX"]);
        Assert.Contains(Path.Combine(_agentHome, ".local", "bin"),
            environment["PATH"].Split(Path.PathSeparator));
    }

    [Fact]
    public void The_orchestrator_creates_nothing_inside_the_agent_home()
    {
        // A prefix directory the orchestrator created would be owned by the
        // orchestrator, in a home whose group the agent is not in, and the agent's
        // own `npm install -g` would fail on it. The entrypoint provisions this
        // scaffolding as the agent instead.
        BuildService().BuildDefaultEnvironment(_stateDirectory);

        Assert.False(Directory.Exists(Path.Combine(_agentHome, ".local")),
            "the orchestrator must not create the npm prefix inside the agent's home");
    }

    [Fact]
    public void A_crossing_with_no_agent_home_keeps_the_prefix_where_it_can_create_it()
    {
        // An agent user without an agent home is a shape the crossing explicitly
        // allows — Route leaves HOME as-is when the home is null. The prefix then
        // falls back to the orchestrator's own home, and the orchestrator both may
        // and must create it: keying the skip on "is isolation on" rather than on
        // containment left `npm install -g` with no prefix at all.
        var orchestratorHome = RedirectOrchestratorHome();

        var environment = BuildService(withAgentHome: false).BuildDefaultEnvironment(_stateDirectory);

        Assert.Equal(orchestratorHome, environment["HOME"]);
        Assert.Equal(Path.Combine(orchestratorHome, ".local"), environment["NPM_CONFIG_PREFIX"]);
        Assert.True(Directory.Exists(Path.Combine(orchestratorHome, ".local", "bin")),
            "the prefix outside the agent's home must still be created");
    }

    [Fact]
    public void The_npm_cache_stays_in_the_shared_state_directory()
    {
        // The one thing under ${STATE_DIR} the agent genuinely writes, which is why
        // that directory stays on the shared scratch root while the logs do not.
        var environment = BuildService().BuildDefaultEnvironment(_stateDirectory);

        Assert.Equal(Path.Combine(_stateDirectory, "npm-cache"), environment["NPM_CONFIG_CACHE"]);
    }
}
