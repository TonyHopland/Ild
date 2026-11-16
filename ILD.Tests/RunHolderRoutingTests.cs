using System.Diagnostics;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.RunHosts.Local;
using ILD.Core.Services.RunHosts;
using ILD.Core.Services.RunHosts.Isolated;
using ILD.Data.Enums;
using ILD.Tests.Fakes;

namespace ILD.Tests;

/// <summary>
/// Which run or chat a command belongs to, as one table: <see cref="RunHolders"/> resolves it once from <see cref="RunCommand.RunIn"/>,
/// <see cref="RunCommand.OwnerId"/>, the working directory and whether the host keeps chats, and the routing host either starts the
/// command there or refuses it, and never starts it on ILD's own machine. Over a fake local host and the isolated host kept in memory.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class RunHolderRoutingTests : IDisposable
{
    private const string Predates = "predates";
    private const string Chat = "chat";
    private const string Run = "run";

    private readonly string? _previousProxyPort = Environment.GetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar);
    private readonly Guid _run = Guid.NewGuid();
    private readonly Guid _chat = Guid.NewGuid();
    private readonly Guid _unknown = Guid.NewGuid();

    public RunHolderRoutingTests() => Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, "3128");

    public void Dispose() => Environment.SetEnvironmentVariable(AgentIsolation.EgressProxyPortEnvVar, _previousProxyPort);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // RunIn, OwnerId, working directory, agent launch, host keeps chats, expected outcome, engine lookups of who holds an id.
    public static TheoryData<string?, string?, string, bool, bool, string, int> Matrix => new()
    {
        // RunIn: the command runs in that run or chat or is refused, never on ILD's own machine.
        { Run, null, "{run}", false, true, Run, 1 },
        { Run, null, "", false, true, Run, 1 },
        { Run, Run, "{run}", false, true, Run, 1 },
        { Run, null, "{other}", false, true, "refusing to run sh in /data/worktrees/x, which is outside run", 1 },
        { Run, null, "{unknown}", false, true, "refusing to run sh in {unknown}, which is outside run", 1 },
        { Run, null, "{other}", true, true, Predates, 0 },
        { "unknown", null, "{unknown}", false, true, "never runs on ILD's own machine", 1 },
        { "unknown", Run, "{run}", false, true, "never runs on ILD's own machine", 1 },
        { Chat, null, "{chat}", false, true, Chat, 1 },
        { Chat, Run, "{chat}", true, true, Chat, 1 },
        { Chat, null, "{chat}", false, false, "holds no run or chat", 1 },
        // No directory: the id alone says whose command it is, a run's or a chat's.
        { null, Run, "", false, true, Run, 1 },
        { null, Chat, "", false, true, Chat, 1 },
        { null, "unknown", "", false, true, Predates, 1 },
        { null, Chat, "", false, false, Predates, 1 },
        { null, null, "", false, true, Predates, 0 },
        // A directory: a chat's folder is the chat's whatever id names it, then the owner as a run, then the run the path is in.
        { null, null, "{run}", false, true, Run, 0 },
        { null, null, "{chat}", false, true, Chat, 0 },
        { null, null, "{chat}", false, false, Predates, 0 },
        { null, Run, "{chat}", false, true, Chat, 0 },
        { null, Run, "{unknown}", false, true, Run, 0 },
        { null, Run, "{other}", false, true, Predates, 0 },
        { null, null, "{other}", false, true, Predates, 0 },
    };

    private Guid IdOf(string? kind) => kind switch { Run => _run, Chat => _chat, "unknown" => _unknown, _ => Guid.Empty };

    private string Expand(string directory) => directory
        .Replace("{run}", RunPaths.WorkingDirectory(_run))
        .Replace("{chat}", RunPaths.ChatRoot(_chat))
        .Replace("{unknown}", RunPaths.WorkingDirectory(_unknown))
        .Replace("{other}", "/data/worktrees/x");

    private static ProcessStartInfo Start(string directory) => new("sh") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task A_command_is_held_by_the_run_or_chat_its_fields_name_or_refused_and_the_engine_is_asked_once(
        string? runIn, string? owner, string directory, bool agent, bool keepsChats, string expected, int lookups)
    {
        var inMemory = new InMemoryRunHost { Credentials = EgressProxyTests.Creds, HoldsRun = id => id == _run, KeepsChats = keepsChats };
        var local = new FakeRunHost();
        var routing = new RoutingRunHost(local, inMemory);
        // The chats share one container, which holds any id once it is up, so it is only made for a row that names a chat.
        if (keepsChats && (runIn == Chat || owner == Chat)) await routing.CreateChatFolderAsync(_chat, "/unused", Ct);
        var executed = inMemory.Execs.Count;
        var lookedUp = inMemory.RunLookups;

        var psi = Start(Expand(directory));
        var command = agent
            ? RunCommand.Agent(psi, IdOf(owner), null, NetworkMode.Monitor, null)
            : RunCommand.Prepared(psi, IdOf(owner));
        if (runIn is not null) command = command with { RunIn = IdOf(runIn) };

        if (expected is Run or Chat)
        {
            using var process = await routing.StartCommandAsync(command, Ct);
            await process.WaitForExitAsync(Ct);
            var name = expected == Run ? inMemory.NameOf(_run) : InMemoryRunHost.ChatName;
            Assert.All(inMemory.Execs.Skip(executed), e => Assert.Equal(name, e.Name));
            Assert.True(inMemory.Execs.Count > executed);
        }
        else if (expected == Predates)
        {
            await Assert.ThrowsAsync<PredatesContainersException>(() => routing.StartCommandAsync(command, Ct));
            Assert.Equal(executed, inMemory.Execs.Count);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => routing.StartCommandAsync(command, Ct));
            Assert.IsNotType<PredatesContainersException>(error);
            Assert.Contains(Expand(expected), error.Message);
            Assert.Equal(executed, inMemory.Execs.Count);
        }

        Assert.Empty(local.Started);
        Assert.Equal(lookups, inMemory.RunLookups - lookedUp);
    }

    [Theory]
    [InlineData("unknown", null, "{run}", "run")]
    [InlineData(null, "run", "{other}", "run")]
    [InlineData(null, "chat", "", "chat")]
    [InlineData(null, null, "", "")]
    public async Task The_isolated_host_called_directly_falls_back_to_the_owner_and_needs_one(string? runIn, string? owner, string directory, string held)
    {
        var inMemory = new InMemoryRunHost { Credentials = EgressProxyTests.Creds, HoldsRun = id => id == _run };
        if (owner == Chat) await inMemory.CreateChatFolderAsync(_chat, "/unused", Ct);
        var command = RunCommand.Prepared(Start(Expand(directory)), IdOf(owner)) with { RunIn = runIn is null ? null : IdOf(runIn) };

        if (held == "")
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => inMemory.StartCommandAsync(command, Ct));
            Assert.Contains("needs the run it belongs to", error.Message);
            return;
        }
        using var process = await inMemory.StartCommandAsync(command, Ct);
        await process.WaitForExitAsync(Ct);
        Assert.Equal(held == "run" ? inMemory.NameOf(_run) : InMemoryRunHost.ChatName, inMemory.Execs.Last().Name);
    }

    [Fact]
    public void A_path_is_held_by_the_run_or_chat_it_names_and_a_chat_on_a_host_that_keeps_none_is_on_ilds_disk()
    {
        var run = RunPaths.WorkingDirectory(_run);
        var chat = RunPaths.ChatRoot(_chat);

        Assert.Equal(RunHolder.Run(_run), RunHolders.FromPath(run, hostsChats: false));
        Assert.Equal(RunHolder.Chat(_chat), RunHolders.FromPath(chat, hostsChats: true));
        Assert.True(RunHolders.FromPath(chat, hostsChats: false).IsOwnDisk);
        Assert.True(RunHolders.FromPath("/data/worktrees/x", hostsChats: true).IsOwnDisk);
        Assert.True(RunHolders.FromPath("", hostsChats: true).IsOwnDisk);
        Assert.Equal(RunHolder.Chat(_chat), RunHolders.Named(chat));
    }

    [Fact]
    public void The_host_answers_where_a_path_is_so_a_caller_never_reads_one()
    {
        var routing = new RoutingRunHost(new FakeRunHost(), new InMemoryRunHost());
        var host = (IRunHost)routing;
        var run = RunPaths.WorkingDirectory(_run);
        var chat = RunPaths.ChatRoot(_chat);

        Assert.Equal(_run, host.RunOf(run));
        Assert.Equal(_chat, host.HolderOf(chat));
        Assert.Null(host.RunOf(chat));
        Assert.Null(host.HolderOf("/data/worktrees/x"));
        Assert.True(host.PredatesContainers("/data/worktrees/x"));
        Assert.False(host.PredatesContainers(run));
        Assert.False(host.PredatesContainers(""));
        Assert.False(host.ChatSeesWorktree(run, "/data/chat-sessions/c"));
        Assert.False(host.ChatSeesWorktree("/data/worktrees/x", chat));
        Assert.True(host.ChatSeesWorktree("/data/worktrees/x", "/data/chat-sessions/c"));
        Assert.Null(host.OwnDiskHostFor(run));
        Assert.NotNull(host.OwnDiskHostFor("/data/worktrees/x"));

        var local = (IRunHost)new FakeRunHost();
        Assert.False(local.PredatesContainers("/data/worktrees/x"));
        Assert.Null(local.OwnDiskHostFor("/data/worktrees/x"));
    }
}
