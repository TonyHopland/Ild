using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;

namespace ILD.Tests;

/// <summary>
/// The bundle the agent writes is untrusted input to the mirror: whatever it holds, only the named branch
/// at a commit that really came out of a well-formed bundle can reach the remote, and nothing is left behind.
/// </summary>
[Collection(GitBundleScratchCollection.Name)]
public class GitMirrorHostileBundleTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "ild-hostile-bundle-" + Guid.NewGuid().ToString("N"));
    private readonly string _remote;
    private readonly string _source;

    public GitMirrorHostileBundleTests()
    {
        Directory.CreateDirectory(_tmp);
        _remote = GitCli.MakeRemote(_tmp, "remote");
        // The agent's clone: main as the remote has it, and a branch of two commits on top.
        _source = Path.Combine(_tmp, "source");
        GitCli.Run(_tmp, "clone", "-q", _remote, _source);
        GitCli.Run(_source, "checkout", "-q", "-b", "feature");
        File.WriteAllText(Path.Combine(_source, "one.txt"), "1\n");
        GitCli.Run(_source, "add", "-A");
        GitCli.Run(_source, "commit", "-m", "one");
        File.WriteAllText(Path.Combine(_source, "two.txt"), "2\n");
        GitCli.Run(_source, "add", "-A");
        GitCli.Run(_source, "commit", "-m", "two");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private GitMirror NewMirror()
    {
        var runner = new ProcessRunner();
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["HOME"] = _tmp,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
        };
        return new GitMirror(
            Path.Combine(_tmp, "mirrors"),
            (directory, args, _, ct, input) => runner.RunAsync("git", args, directory, ct, environment, new ProcessRunOptions(CleanEnvironment: true, StandardInputFrom: input)));
    }

    /// <summary>A bundle git writes in the agent's clone from <paramref name="args"/>.</summary>
    private byte[] Bundle(params string[] args)
    {
        var file = Path.Combine(_tmp, Guid.NewGuid().ToString("N") + ".bundle");
        GitCli.Run(_source, ["bundle", "create", "--quiet", file, .. args]);
        return File.ReadAllBytes(file);
    }

    private async Task AssertRejectedAsync(byte[] bundle)
    {
        var ct = TestContext.Current.CancellationToken;
        var (session, openError) = await NewMirror().OpenAsync("https://forge.example.test/team/app.git", ct);
        Assert.NotNull(session);
        Assert.Null(openError);
        using (session)
        {
            var sha = GitCli.Out(_source, "rev-parse", "feature");
            var (ok, error) = await session.PushAsync(
                new GitAuthOptions(_remote, null), "feature", sha,
                async (_, sink) =>
                {
                    await sink.WriteAsync(bundle, ct);
                    return new ProcessResult(0, string.Empty, string.Empty);
                },
                ct);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Equal("", GitCli.Out(_remote, "for-each-ref", "refs/heads/feature"));
            Assert.Equal("", GitCli.Out(session.Path, "for-each-ref", "refs/ild-push", "refs/heads/feature"));
            Assert.Empty(AuditingRunner.BundleFilesUnderPrivateRoot());
        }
    }

    [Fact]
    public async Task A_bundle_without_the_branch_is_rejected() => await AssertRejectedAsync(Bundle("main"));

    [Fact]
    public async Task A_bundle_whose_only_refs_are_others_is_rejected()
    {
        GitCli.Run(_source, "tag", "feature-tag", "feature");
        await AssertRejectedAsync(Bundle("refs/tags/feature-tag", "main"));
    }

    [Fact]
    public async Task A_bundle_that_builds_on_a_commit_the_mirror_lacks_is_rejected()
        => await AssertRejectedAsync(Bundle("feature", "^feature~1"));

    [Fact]
    public async Task Something_that_is_not_a_bundle_is_rejected()
        => await AssertRejectedAsync(System.Text.Encoding.ASCII.GetBytes("#!/bin/sh\necho owned\n"));

    [Fact]
    public async Task A_bundle_with_a_good_header_and_a_corrupt_pack_is_rejected()
    {
        var real = Bundle("feature");
        var end = System.Text.Encoding.ASCII.GetString(real).IndexOf("\n\n", StringComparison.Ordinal) + 2;
        var hostile = real[..end].Concat(new byte[] { 0x50, 0x41, 0x43, 0x4b, 0xde, 0xad, 0xbe, 0xef, 0, 1, 2, 3 }).ToArray();
        await AssertRejectedAsync(hostile);
    }

    [Fact]
    public async Task A_good_bundle_is_taken_and_pushed_so_the_rejections_above_are_the_bundles_not_the_setup()
    {
        var ct = TestContext.Current.CancellationToken;
        var bundle = Bundle("feature");
        var (session, _) = await NewMirror().OpenAsync("https://forge.example.test/team/app.git", ct);
        using (session!)
        {
            var sha = GitCli.Out(_source, "rev-parse", "feature");
            var (ok, error) = await session!.PushAsync(
                new GitAuthOptions(_remote, null), "feature", sha,
                async (_, sink) =>
                {
                    await sink.WriteAsync(bundle, ct);
                    return new ProcessResult(0, string.Empty, string.Empty);
                },
                ct);

            Assert.True(ok, error);
            Assert.Equal(sha, GitCli.Out(_remote, "rev-parse", "refs/heads/feature"));
        }
    }
}
