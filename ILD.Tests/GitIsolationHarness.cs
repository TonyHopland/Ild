using System.Text;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// Stands between <see cref="RepositoryManager"/> and the real <see cref="ProcessRunner"/>:
/// every git call is checked against the start info <see cref="ProcessRunner.BuildStartInfo"/>
/// would produce for it, then executed for real.
///
/// <para>
/// A call that asked for a <see cref="ProcessRunOptions.CleanEnvironment"/> is the
/// orchestrator's own (mirror operations, ls-remote); every other git call acts on
/// an agent-writable repository and must be an agent call. The host cannot really
/// <c>setpriv --reuid</c>, so in isolated mode the wrap is verified on the start
/// info and the call is then run with the uid crossing removed (and tagged with
/// <see cref="RouteVariable"/> so a planted hook can say who ran it).
/// </para>
/// </summary>
internal sealed class AuditingRunner : IProcessRunner
{
    public const string RouteVariable = "ILD_TEST_ROUTE";
    public const string Setpriv = "/usr/bin/setpriv";

    private static readonly string[] OrchestratorAllowedKeys =
    {
        "PATH", "HOME", "GIT_TERMINAL_PROMPT", "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_COUNT", "GIT_ASKPASS", "ILD_GIT_USERNAME", "ILD_GIT_PASSWORD",
        "GIT_CEILING_DIRECTORIES", RouteVariable,
        "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy",
    };

    private readonly IProcessRunner _inner;
    private readonly bool _tagRoutes;
    private readonly object _gate = new();
    private readonly List<string> _violations = new();
    private readonly List<(string Path, UnixFileMode Mode)> _bundlesSeen = new();
    private readonly List<string> _forbiddenRoots = new();

    public AuditingRunner(bool isolated, bool tagRoutes = false, IProcessRunner? inner = null)
    {
        Isolated = isolated;
        Agent = new AgentIdentity("ild-agent", "ild-agents", Path.Combine(Path.GetTempPath(), "ild-agent-home"));
        _tagRoutes = tagRoutes;
        _inner = inner ?? new ProcessRunner();
        Seed = BuildSeed();
    }

    public bool Isolated { get; }

    /// <summary>The agent identity to give <see cref="RepositoryManager"/> in isolated mode, null otherwise.</summary>
    public AgentIdentity Agent { get; }

    public AgentIdentity? ManagerAgent => Isolated ? Agent : null;

    /// <summary>What the orchestrator's environment looks like to the start-info check: every secret, topology key and git credential present.</summary>
    public Dictionary<string, string> Seed { get; }

    public IReadOnlyList<string> Violations { get { lock (_gate) return _violations.ToList(); } }

    public IReadOnlyList<(string Path, UnixFileMode Mode)> BundlesSeen { get { lock (_gate) return _bundlesSeen.ToList(); } }

    public sealed record GitCall(
        IReadOnlyList<string> Args, string? Cwd, bool Orchestrator, IReadOnlyDictionary<string, string?>? Environment, ProcessRunOptions? Options);

    private readonly List<GitCall> _calls = new();

    /// <summary>Every git call in order, with the explicit environment the caller handed the runner.</summary>
    public IReadOnlyList<GitCall> Calls { get { lock (_gate) return _calls.ToList(); } }

    /// <summary>Paths in agent-writable repositories: no orchestrator-class git call may run in or name them.</summary>
    public void Forbid(string path) { lock (_gate) _forbiddenRoots.Add(Path.GetFullPath(path)); }

    public void AssertClean()
    {
        var violations = Violations;
        Assert.True(violations.Count == 0, "git isolation violated:\n" + string.Join("\n", violations));
    }

    private static Dictionary<string, string> BuildSeed()
    {
        var seed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            seed[(string)e.Key] = (string?)e.Value ?? string.Empty;
        foreach (var key in AgentIsolation.SecretEnvironmentKeys)
            seed[key] = "orchestrator-secret-" + key;
        foreach (var key in AgentIsolation.OrchestratorTopologyEnvKeys)
            seed[key] = "orchestrator-topology-" + key;
        seed["GIT_ASKPASS"] = "/orchestrator/askpass.sh";
        seed["ILD_GIT_USERNAME"] = "orchestrator-git-user";
        seed["ILD_GIT_PASSWORD"] = "orchestrator-git-password";
        seed["GIT_AUTHOR_NAME"] = "Seed Author";
        seed["GIT_AUTHOR_EMAIL"] = "author@example.test";
        seed["GIT_COMMITTER_NAME"] = "Seed Committer";
        seed["GIT_COMMITTER_EMAIL"] = "committer@example.test";
        return seed;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        ProcessRunOptions? options = null)
    {
        var orchestrator = options?.CleanEnvironment == true;
        if (fileName == "git")
        {
            lock (_gate)
                _calls.Add(new GitCall(args.ToList(), workingDirectory, orchestrator,
                    environmentVariables is null ? null : new Dictionary<string, string?>(environmentVariables), options));
            Audit(args, workingDirectory, environmentVariables, options, orchestrator);
        }

        var forwarded = environmentVariables is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(environmentVariables, StringComparer.Ordinal);
        // An agent-scoped script (the pre-push hook runner) is held to the agent rules and tagged like git.
        var agentScript = fileName != "git" && options?.AgentScoped == true;
        if (agentScript)
            AuditAgentScript(fileName, args, workingDirectory, environmentVariables, options);
        if (_tagRoutes && (fileName == "git" || agentScript))
            forwarded[RouteVariable] = orchestrator ? "orchestrator" : "agent";

        var executed = options is null ? null : options with { AsAgent = null };
        return await _inner.RunAsync(fileName, args, workingDirectory, ct, forwarded, executed);
    }

    private void Audit(
        IReadOnlyList<string> args, string? cwd, IReadOnlyDictionary<string, string?>? env,
        ProcessRunOptions? options, bool orchestrator)
    {
        var label = $"git {string.Join(' ', args)} (cwd={cwd ?? "<none>"})";
        var psi = ProcessRunner.BuildStartInfo("git", args, cwd, env, options, Isolated ? Agent.User : null, Seed);
        var environment = psi.Environment.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
        var argv = psi.ArgumentList.ToList();

        if (!Isolated && psi.FileName == Setpriv)
            Violate($"{label}: setpriv used outside isolated mode");

        if (orchestrator)
            AuditOrchestrator(label, args, cwd, psi.FileName, argv, environment);
        else
            AuditAgent(label, psi.FileName, argv, environment);
    }

    private void AuditAgentScript(
        string fileName, IReadOnlyList<string> args, string? cwd, IReadOnlyDictionary<string, string?>? env, ProcessRunOptions? options)
    {
        var psi = ProcessRunner.BuildStartInfo(fileName, args, cwd, env, options, Isolated ? Agent.User : null, Seed);
        var environment = psi.Environment.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
        var label = $"{fileName} {string.Join(' ', args.Take(2))} (cwd={cwd ?? "<none>"})";
        if (!Isolated && psi.FileName == Setpriv)
            Violate($"{label}: setpriv used outside isolated mode");
        AuditAgent(label, psi.FileName, psi.ArgumentList.ToList(), environment, fileName);
    }

    private void AuditAgent(string label, string file, List<string> argv, Dictionary<string, string> environment, string command = "git")
    {
        var banned = AgentIsolation.SecretEnvironmentKeys
            .Concat(AgentIsolation.OrchestratorTopologyEnvKeys)
            .Concat(new[] { "GIT_ASKPASS", "ILD_GIT_USERNAME", "ILD_GIT_PASSWORD" })
            .Where(environment.ContainsKey)
            .ToList();
        if (banned.Count > 0)
            Violate($"{label}: agent call inherits {string.Join(", ", banned)}");

        foreach (var key in new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL" })
        {
            if (!environment.TryGetValue(key, out var value) || value != Seed[key])
                Violate($"{label}: agent call lost {key}");
        }

        if (!Isolated) return;

        var dashDash = argv.IndexOf("--");
        if (file != Setpriv || !argv.Contains($"--reuid={Agent.User}") || dashDash < 0 || dashDash + 1 >= argv.Count || argv[dashDash + 1] != command)
            Violate($"{label}: not launched as the agent (start info: {file} {string.Join(' ', argv.Take(8))})");
    }

    private void AuditOrchestrator(
        string label, IReadOnlyList<string> args, string? cwd, string file, List<string> argv, Dictionary<string, string> environment)
    {
        if (argv.Any(a => a.StartsWith("--reuid", StringComparison.Ordinal)))
            Violate($"{label}: orchestrator call crosses to another uid");

        var extra = environment.Keys
            .Where(k => !OrchestratorAllowedKeys.Contains(k)
                        && !k.StartsWith("GIT_CONFIG_KEY_", StringComparison.Ordinal)
                        && !k.StartsWith("GIT_CONFIG_VALUE_", StringComparison.Ordinal))
            .ToList();
        if (extra.Count > 0)
            Violate($"{label}: orchestrator environment is not built from scratch, extra: {string.Join(", ", extra)}");

        foreach (var (key, expected) in new[]
                 {
                     ("GIT_TERMINAL_PROMPT", "0"), ("GIT_CONFIG_NOSYSTEM", "1"), ("GIT_CONFIG_GLOBAL", "/dev/null"),
                 })
        {
            if (!environment.TryGetValue(key, out var value) || value != expected)
                Violate($"{label}: {key} must be {expected}");
        }

        if (!environment.ContainsKey("PATH")) Violate($"{label}: PATH missing");

        if (!environment.TryGetValue("HOME", out var home) || !Directory.Exists(home))
            Violate($"{label}: HOME must name an existing directory");
        else if (!OwnerOnly(home))
            Violate($"{label}: HOME {home} is reachable by others");

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (environment.TryGetValue("GIT_CONFIG_COUNT", out var countText) && int.TryParse(countText, out var count))
        {
            for (var i = 0; i < count; i++)
            {
                if (environment.TryGetValue($"GIT_CONFIG_KEY_{i}", out var key))
                    config[key] = environment.GetValueOrDefault($"GIT_CONFIG_VALUE_{i}", "<missing>");
            }
        }

        foreach (var (key, expected) in new[]
                 {
                     ("core.hooksPath", "/dev/null"), ("core.fsmonitor", "false"), ("credential.helper", ""),
                     ("core.sshCommand", ""), ("protocol.ext.allow", "never"),
                 })
        {
            if (!config.TryGetValue(key, out var value) || value != expected)
                Violate($"{label}: GIT_CONFIG_* must set {key}={expected}");
        }

        if (args.Contains("ls-remote"))
        {
            if (cwd is null || !OwnerOnly(cwd))
                Violate($"{label}: ls-remote must run in an owner-only private directory");
            if (!environment.ContainsKey("GIT_CEILING_DIRECTORIES"))
                Violate($"{label}: ls-remote must set GIT_CEILING_DIRECTORIES");
        }

        if (cwd is not null && IsForbidden(cwd))
            Violate($"{label}: orchestrator runs git inside an agent-writable repository");

        foreach (var arg in args.Where(a => a.Length > 0))
        {
            if (IsForbidden(arg.Contains('=') ? arg[(arg.IndexOf('=') + 1)..] : arg))
                Violate($"{label}: orchestrator git names an agent-writable path ({arg})");
        }

        RecordBundles();
    }

    private bool IsForbidden(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return false; }
        lock (_gate)
            return _forbiddenRoots.Any(root => full == root || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static bool OwnerOnly(string path)
        => !OperatingSystem.IsLinux() || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                                                      | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;

    private void RecordBundles()
    {
        foreach (var path in BundleFilesUnderPrivateRoot())
        {
            lock (_gate)
                _bundlesSeen.Add((path, File.GetUnixFileMode(path)));
        }
    }

    /// <summary>Files under the orchestrator-private root that start like a git bundle.</summary>
    public static IReadOnlyList<string> BundleFilesUnderPrivateRoot()
    {
        var found = new List<string>();
        var root = AgentIsolation.PrivateRoot;
        if (!Directory.Exists(root)) return found;
        var signature = Encoding.ASCII.GetBytes("# v");
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var head = new byte[16];
                var read = stream.Read(head, 0, head.Length);
                var text = Encoding.ASCII.GetString(head, 0, read);
                if (read >= signature.Length && text.StartsWith("# v", StringComparison.Ordinal) && text.Contains("git bundle", StringComparison.Ordinal))
                    found.Add(file);
            }
            catch (IOException) { /* gone or locked: not ours to read */ }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    private void Violate(string message)
    {
        lock (_gate) _violations.Add(message);
    }
}

/// <summary>Plain git for arranging a test's own repositories; never goes near the code under test.</summary>
internal static class GitCli
{
    public static string Out(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-c", "user.name=Tester", "-c", "user.email=t@t.io", "-c", "protocol.file.allow=always" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {stderr}");
        return stdout.Trim();
    }

    public static void Run(string cwd, params string[] args) => Out(cwd, args);

    /// <summary>A bare repository with one commit on main, as a remote.</summary>
    public static string MakeRemote(string root, string name)
    {
        var bare = Path.Combine(root, name + ".git");
        Directory.CreateDirectory(bare);
        Run(bare, "init", "--bare", "-b", "main");
        var seed = Path.Combine(root, name + "-seed");
        Directory.CreateDirectory(seed);
        Run(seed, "init", "-b", "main");
        File.WriteAllText(Path.Combine(seed, "README.md"), "hello\n");
        Run(seed, "add", "-A");
        Run(seed, "commit", "-m", "init");
        Run(seed, "push", bare, "main");
        return bare;
    }
}
