using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;

namespace ILD.Tests;

/// <summary>
/// git ls-remote matches its pattern as a glob against the tail of each ref
/// name, so the repository test must not read a different branch as the
/// default one. Real git against a local repository.
/// </summary>
[Collection("Git")]
public class ConnectionTesterBranchMatchTests : IDisposable
{
    private readonly string _origin = Path.Combine(Path.GetTempPath(), "ild-branchmatch-" + Guid.NewGuid().ToString("N"));

    public ConnectionTesterBranchMatchTests()
    {
        Directory.CreateDirectory(_origin);
        Git("init", "-b", "main-x");
        File.WriteAllText(Path.Combine(_origin, "README.md"), "hi\n");
        Git("add", "-A");
        Git("-c", "user.email=t@t.io", "-c", "user.name=Tester", "commit", "-m", "init");
        Git("branch", "x/refs/heads/main");
    }

    public void Dispose()
    {
        try { Directory.Delete(_origin, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _origin, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private Task<ConnectionTestResult> TestAsync(string defaultBranch)
    {
        var tester = new ConnectionTester(
            [new ForgejoRemoteGitProviderAdapter(), new GitHubRemoteGitProviderAdapter(), new AzureDevOpsRemoteGitProviderAdapter()],
            new RepositoryManager(),
            new HttpClient());
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "app",
            CloneUrl = _origin,
            DefaultBranch = defaultBranch,
            RemoteProviderId = Guid.NewGuid(),
        };
        return tester.TestRepositoryAsync(repo, null, CancellationToken.None);
    }

    [Theory]
    [InlineData("main*")]
    [InlineData("main")]
    [InlineData("mai?-x")]
    public async Task A_default_branch_that_only_matches_another_branch_as_a_pattern_is_missing(string defaultBranch)
    {
        var result = await TestAsync(defaultBranch);

        Assert.Equal(ConnectionTestOutcome.BranchMissing, result.Outcome);
        Assert.Contains($"'{defaultBranch}'", result.Message);
    }

    [Theory]
    [InlineData("main-x")]
    [InlineData("x/refs/heads/main")]
    public async Task A_default_branch_that_exists_by_its_exact_name_is_ok(string defaultBranch)
    {
        var result = await TestAsync(defaultBranch);

        Assert.Equal(ConnectionTestOutcome.Ok, result.Outcome);
    }
}
