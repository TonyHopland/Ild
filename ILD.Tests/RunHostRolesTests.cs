using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Local;
using ILD.Tests.Fakes;
using Moq;

namespace ILD.Tests;

/// <summary>That callers get the role they use of the one run host, and that a backend without a role has none rather than a refusal.</summary>
public class RunHostRolesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_local_backend_has_no_sweep_and_no_sign_in_container_and_routes_its_own_terminal_command()
    {
        var local = new LocalRunHost();

        var selection = RunHostSelection.For(local);

        Assert.Same(local, selection.Host);
        Assert.Same(local, selection.Operator);
        Assert.Null(selection.Sweep);
        Assert.Null(selection.SignIn);
        Assert.Same(local, selection.LocalTerminalCommands);
    }

    [Fact]
    public async Task The_local_operator_view_says_nothing_of_containers()
    {
        IRunHostOperator op = new LocalRunHost();

        Assert.Equal(RunHostKind.Local, op.Kind);
        Assert.Null(await op.WarningAsync(Ct));
        Assert.Empty(await op.DirectRoutesAsync(Ct));
        Assert.Empty(await op.IsolatedRangesAsync(Ct));
        Assert.Null(op.AttachTargetFor("/anywhere"));
        await op.ShutdownAsync(Ct);
    }

    [Fact]
    public async Task The_sweeper_needs_only_the_sweep_role()
    {
        var sweep = new Mock<IRunSweep>();
        sweep.SetupGet(s => s.SweepInterval).Returns(TimeSpan.FromMinutes(10));
        var warmed = new TaskCompletionSource();
        sweep.Setup(s => s.WarmAgentToolsAsync(It.IsAny<CancellationToken>())).Callback(() => warmed.TrySetResult()).Returns(Task.CompletedTask);
        var sweeper = new RunHostSweeper(sweep.Object, _ => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));

        await sweeper.StartAsync(Ct);
        await warmed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await sweeper.StopAsync(Ct);

        sweep.Verify(s => s.CheckAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task The_sweeper_of_a_backend_without_a_sweep_does_nothing()
    {
        var sweeper = new RunHostSweeper(null, _ => throw new InvalidOperationException("nothing to sweep"));

        await sweeper.StartAsync(Ct);
        await sweeper.StopAsync(Ct);
    }

    [Fact]
    public async Task A_file_open_for_read_answers_null_for_a_directory_and_for_nothing()
    {
        IRunFiles files = new LocalRunHost();
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var file = Path.Combine(dir.FullName, "ild.config.json");
            await File.WriteAllTextAsync(file, "{}", Ct);

            await using (var read = await files.OpenFileForReadAsync(file, Ct))
                Assert.Equal("{}", await new StreamReader(read!).ReadToEndAsync(Ct));
            Assert.Null(await files.OpenFileForReadAsync(dir.FullName, Ct));
            Assert.Null(await files.OpenFileForReadAsync(Path.Combine(dir.FullName, "none"), Ct));
        }
        finally { dir.Delete(true); }
    }
}
