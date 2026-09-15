using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A pi chat turn leaves the ILD extension (it holds the ILD API token) in the
/// agent read root and pi's own agent and session directories in shared scratch.
/// Deleting the chat removes all of them. Since nothing can find the extension
/// once the chat row is gone, failing to remove it keeps the chat; nothing the
/// agent planted in its own directories may ever block a delete.
/// </summary>
public sealed class ChatServiceIldExtensionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-ext-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _cleanup = new();

    public void Dispose()
    {
        _db.Dispose();
        foreach (var dir in _cleanup.Append(_scratchRoot))
        {
            if (OperatingSystem.IsLinux() && Directory.Exists(dir))
            {
                foreach (var nested in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetUnixFileMode(nested, File.GetUnixFileMode(nested) | UnixFileMode.UserWrite); } catch { }
                }
            }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task Deleting_a_chat_removes_its_pi_extension_and_directories()
    {
        var svc = NewService();
        var chatId = await StartChatAsync(svc);
        var (extension, agentDir, sessionDir) = WritePiFiles(chatId);

        Assert.True(await svc.DeleteAsync("alice", chatId));

        Assert.False(Directory.Exists(extension), "the chat's ild.ts, which holds the API token, was left behind");
        Assert.False(Directory.Exists(agentDir));
        Assert.False(Directory.Exists(sessionDir));
        Assert.Empty(_db.Context.ChatSessions);
    }

    [Fact]
    public async Task A_chat_whose_pi_extension_cannot_be_removed_is_kept_for_a_retry()
    {
        // root can delete a read-only tree, so the failure cannot be staged there.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;

        var svc = NewService();
        var chatId = await StartChatAsync(svc);
        var (extension, _, _) = WritePiFiles(chatId);
        LockFolderIn(extension);

        var error = await Record.ExceptionAsync(() => svc.DeleteAsync("alice", chatId));

        Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected {error?.GetType().Name ?? "success"}");
        Assert.Single(_db.Context.ChatSessions);
    }

    [Fact]
    public async Task Read_only_folders_the_agent_planted_never_block_deleting_all_chats()
    {
        var svc = NewService();
        var first = await StartChatAsync(svc);
        var second = await StartChatAsync(svc);
        var firstFiles = WritePiFiles(first);
        var secondFiles = WritePiFiles(second);
        LockFolderIn(firstFiles.AgentDir);
        LockFolderIn(secondFiles.SessionDir);

        Assert.Equal(2, await svc.DeleteAllForUserAsync("alice"));

        Assert.Empty(_db.Context.ChatSessions);
        Assert.False(Directory.Exists(firstFiles.AgentDir));
        Assert.False(Directory.Exists(secondFiles.SessionDir));
    }

    private ChatService NewService()
    {
        var adapter = Mock.Of<IAgentAdapter>();
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));
        return new ChatService(
            _db.Context, _db.Providers, registry, Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = _scratchRoot }, _db.LoopRuns, new ChatLoopScratchpad());
    }

    private async Task<Guid> StartChatAsync(ChatService svc)
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "pi",
            Type = "pi",
            BaseUrl = "http://localhost",
            Model = "m",
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AiProviders.Add(provider);
        await _db.Context.SaveChangesAsync();
        return (await svc.StartAsync("alice", provider.Id, null)).Id;
    }

    private (string Extension, string AgentDir, string SessionDir) WritePiFiles(Guid chatId)
    {
        var id = chatId.ToString("N");
        var extension = AgentIsolation.CreateAgentReadDirectory("ild-pi-ext", id);
        File.WriteAllText(Path.Combine(extension, "ild.ts"), "const CONFIG = { env: { ILD_API_TOKEN: \"t\" } };");
        var agentDir = Directory.CreateDirectory(Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-agent", id)).FullName;
        var sessionDir = Directory.CreateDirectory(Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-sessions", id)).FullName;
        _cleanup.AddRange([extension, agentDir, sessionDir]);
        return (extension, agentDir, sessionDir);
    }

    private static void LockFolderIn(string directory)
    {
        var locked = Directory.CreateDirectory(Path.Combine(directory, "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "file"), "x");
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }
}
