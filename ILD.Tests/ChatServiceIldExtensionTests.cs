using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A pi chat turn leaves <c>ild-pi-ext/&lt;session&gt;/ild.ts</c>, which holds the
/// ILD API token. Deleting the chat must remove it, and since nothing can find
/// it once the chat row is gone, a failed removal must keep the chat.
/// </summary>
public sealed class ChatServiceIldExtensionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "ild-chat-ext-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
    }

    [Fact]
    public async Task Deleting_a_chat_removes_its_pi_ild_extension()
    {
        var (svc, chatId) = await StartChatAsync();
        var extension = WriteExtension(chatId);

        Assert.True(await svc.DeleteAsync("alice", chatId));

        Assert.False(Directory.Exists(extension), "the chat's ild.ts, which holds the API token, was left behind");
        Assert.Empty(_db.Context.ChatSessions);
    }

    [Fact]
    public async Task A_chat_whose_pi_ild_extension_cannot_be_removed_is_kept_for_a_retry()
    {
        // root can delete a read-only tree, so the failure cannot be staged there.
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;

        var (svc, chatId) = await StartChatAsync();
        var extension = WriteExtension(chatId);
        var locked = Path.Combine(extension, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "ild.ts"), "token");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var error = await Record.ExceptionAsync(() => svc.DeleteAsync("alice", chatId));

            Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected {error?.GetType().Name ?? "success"}");
            Assert.Single(_db.Context.ChatSessions);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(extension, recursive: true);
        }
    }

    private async Task<(ChatService Service, Guid ChatId)> StartChatAsync()
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

        var adapter = Mock.Of<IAgentAdapter>();
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));
        var svc = new ChatService(
            _db.Context, _db.Providers, registry, Mock.Of<IChatNotifier>(),
            new ChatOptions { ScratchRoot = _scratchRoot }, _db.LoopRuns, new ChatLoopScratchpad());

        var chat = await svc.StartAsync("alice", provider.Id, null);
        return (svc, chat.Id);
    }

    private static string WriteExtension(Guid chatId)
    {
        var extension = Path.Combine(AgentIsolation.ScratchRoot, "ild-pi-ext", chatId.ToString("N"));
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "ild.ts"), "const CONFIG = { env: { ILD_API_TOKEN: \"t\" } };");
        return extension;
    }
}
