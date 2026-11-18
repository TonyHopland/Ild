using System.Diagnostics;
using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

/// <summary>
/// What a run's checkpoint keeps and what restoring it gives back, with real git: the scripts a host runs in the pod, on two clones standing for
/// the pod that was lost and the one made in its place. A branch is never involved: the checkpoint is a hidden ref.
/// </summary>
public sealed class RunCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ild-checkpoint-{Guid.NewGuid():N}");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public RunCheckpointTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static (int Code, byte[] Out, string Err) Run(string file, string? cwd, byte[]? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd ?? "/", RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "T";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "t@example.test";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        if (stdin is not null) { p.StandardInput.BaseStream.Write(stdin); p.StandardInput.Close(); }
        using var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        return (p.ExitCode, output.ToArray(), err.Result);
    }

    private static string Git(string cwd, params string[] args)
    {
        var (code, output, err) = Run("git", cwd, null, args);
        Assert.True(code == 0, $"git {string.Join(' ', args)}: {err}");
        return System.Text.Encoding.UTF8.GetString(output).Trim();
    }

    [Fact]
    public void A_checkpoint_keeps_commits_and_uncommitted_and_untracked_work_but_not_ignored_files_and_restores_them_in_a_new_clone()
    {
        var baseRepo = Path.Combine(_root, "base");
        Directory.CreateDirectory(baseRepo);
        Git(baseRepo, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(baseRepo, ".gitignore"), ".env\n");
        File.WriteAllText(Path.Combine(baseRepo, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(baseRepo, "gone.txt"), "gone");
        Git(baseRepo, "add", "-A");
        Git(baseRepo, "commit", "-q", "-m", "first");

        // The pod that is later lost: a commit, an edit, an untracked file, a deleted file and a secret git ignores.
        var lost = Path.Combine(_root, "lost");
        Git(_root, "clone", "-q", baseRepo, lost);
        Git(lost, "checkout", "-q", "-b", "ild/wi-1-run");
        File.WriteAllText(Path.Combine(lost, "committed.txt"), "committed");
        Git(lost, "add", "-A");
        Git(lost, "commit", "-q", "-m", "work");
        File.WriteAllText(Path.Combine(lost, "keep.txt"), "edited");
        File.WriteAllText(Path.Combine(lost, "new.txt"), "untracked");
        File.WriteAllText(Path.Combine(lost, ".env"), "SECRET=1");
        File.Delete(Path.Combine(lost, "gone.txt"));
        var head = Git(lost, "rev-parse", "HEAD");

        var (code, bundle, err) = Run("/bin/bash", null, null, "-c", ExecRunHost.CheckpointScript, "bash", lost);
        Assert.True(code == 0, err);
        var runRef = "refs/ild/runs/abc";
        var bundlePath = Path.Combine(_root, "checkpoint.bundle");
        File.WriteAllBytes(bundlePath, bundle);
        Git(baseRepo, "fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "--", bundlePath, $"+refs/ild/checkpoint:{runRef}");

        // The run's own branch, index and files are as they were: the checkpoint used a temporary index.
        Assert.Equal(head, Git(lost, "rev-parse", "HEAD"));
        Assert.Contains("keep.txt", Git(lost, "status", "--porcelain"));
        Assert.DoesNotContain("ild/wi-1-run", Git(baseRepo, "for-each-ref", "refs/heads", "refs/remotes"));
        Assert.DoesNotContain("SECRET", Git(baseRepo, "ls-tree", "-r", "--name-only", runRef));

        // The pod made in its place: the branch checked out fresh, then the checkpoint put back.
        var again = Path.Combine(_root, "again");
        Git(_root, "clone", "-q", baseRepo, again);
        var restoreBundle = Run("git", baseRepo, null, "bundle", "create", "--quiet", "-", runRef).Out;
        var (restored, _, restoreErr) = Run("/bin/bash", null, restoreBundle, "-c", ExecRunHost.RestoreScript, "bash", again, "ild/wi-1-run", runRef);
        Assert.True(restored == 0, restoreErr);

        Assert.Equal(head, Git(again, "rev-parse", "HEAD"));
        Assert.Equal("ild/wi-1-run", Git(again, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("committed", File.ReadAllText(Path.Combine(again, "committed.txt")));
        Assert.Equal("edited", File.ReadAllText(Path.Combine(again, "keep.txt")));
        Assert.Equal("untracked", File.ReadAllText(Path.Combine(again, "new.txt")));
        Assert.False(File.Exists(Path.Combine(again, "gone.txt")));
        Assert.False(File.Exists(Path.Combine(again, ".env")));
        var status = Git(again, "status", "--porcelain");
        Assert.Contains(" M keep.txt", status);
        Assert.Contains("D gone.txt", status);
        Assert.Contains("?? new.txt", status);
    }
}
