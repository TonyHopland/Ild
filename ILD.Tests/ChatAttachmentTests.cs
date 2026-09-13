using System.Text;
using ILD.Core.Services.Attachments;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Attaching a file to a chat turn, end to end through the service.
///
/// <para>
/// The durable copy is a database row, not a file: a chat's scratch directory is
/// readable by the agent uid and one uid serves every chat, so anything left
/// there for the life of a chat is readable by every later agent. The bytes are
/// written into that directory only for the turn that carries them, because the
/// adapter passes the prompt through unchanged and an agent can open a path but
/// not a blob — and they are taken away again when the turn ends.
/// </para>
/// </summary>
public sealed class ChatAttachmentTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ChatLoopScratchpad _loopScratchpad = new();
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(), "ild-chat-attachment-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// Records the prompt, and captures what was on disk *while the agent ran* —
    /// which is the only moment an attachment is supposed to be a file.
    /// </summary>
    private sealed class CapturingAdapter : IAgentAdapter
    {
        public AgentExecutionContext? LastContext { get; private set; }
        public List<string> FilesVisibleDuringTurn { get; } = new();
        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            LastContext = context;
            var uploads = Path.Combine(context.RunContext.WorktreePath, "uploads");
            if (Directory.Exists(uploads)) FilesVisibleDuringTurn.AddRange(Directory.GetFiles(uploads));
            return Task.FromResult(NodeExecutionResult.Ok("ack"));
        }
    }

    private sealed class NoopNotifier : IChatNotifier
    {
        public Task MessageAppendedAsync(Guid id, ChatMessageView m) => Task.CompletedTask;
        public Task TurnProgressAsync(Guid id, string delta) => Task.CompletedTask;
        public Task TurnCompletedAsync(Guid id, bool interrupted) => Task.CompletedTask;
        public Task LoopUpdateRequestedAsync(Guid id, string document) => Task.CompletedTask;
    }

    private async Task<(ChatService Service, CapturingAdapter Adapter, ChatSessionView Session)> StartChatAsync()
    {
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "p1",
            Type = "claude-code",
            BaseUrl = "http://localhost",
            Model = "m",
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Context.AiProviders.Add(provider);
        await _db.Context.SaveChangesAsync();

        var adapter = new CapturingAdapter();
        var registry = Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter));

        var service = new ChatService(
            _db.Context, _db.Providers, registry, new NoopNotifier(),
            new ChatOptions { ScratchRoot = _scratchRoot }, _db.LoopRuns, _loopScratchpad);

        var session = await service.StartAsync("alice", provider.Id, ["ild", "read"]);
        return (service, adapter, session);
    }

    private static UploadedFile Upload(string name, string content, string? contentType = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new UploadedFile(name, contentType, bytes.Length, new MemoryStream(bytes));
    }

    private static IReadOnlyList<Guid> Ids(IReadOnlyList<AttachmentView>? saved)
        => saved!.Select(a => Guid.Parse(a.Id)).ToList();

    private string ScratchPath => _db.Context.ChatSessions.Single().ScratchPath;

    private Task RunTurnAsync(ChatService service, Guid sessionId, string message, IReadOnlyList<AttachmentView>? saved)
        => service.ExecuteTurnAsync(
            sessionId, message, openWorkItemId: null, openLoopDocument: null, Ids(saved), CancellationToken.None);

    [Fact]
    public async Task An_uploaded_file_is_stored_in_the_database_and_not_on_disk()
    {
        var (service, _, session) = await StartChatAsync();

        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);

        var view = Assert.Single(saved!);
        Assert.Equal("sketch.png", view.FileName);
        Assert.Equal(6, view.SizeBytes);

        var row = _db.Context.ChatAttachments.Single();
        Assert.Equal("pixels", Encoding.UTF8.GetString(row.Content));
        // Nothing on disk until a turn needs it there.
        Assert.Empty(Directory.GetFiles(Path.Combine(ScratchPath, "uploads")));
    }

    [Fact]
    public async Task Saving_attachments_for_another_users_chat_stores_nothing()
    {
        var (service, _, session) = await StartChatAsync();

        var saved = await service.SaveAttachmentsAsync("mallory", session.Id, [Upload("sketch.png", "pixels")]);

        Assert.Null(saved);
        Assert.Empty(_db.Context.ChatAttachments);
    }

    [Fact]
    public async Task The_agent_gets_a_real_file_for_the_turn_and_nothing_is_left_afterwards()
    {
        var (service, adapter, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync(
            "alice", session.Id, [Upload("sketch.png", "pixels", "image/png"), Upload("run.log", "boom")]);

        await RunTurnAsync(service, session.Id, "what is wrong here?", saved);

        // While the agent ran, both files existed and the prompt named them by
        // absolute path — an agent can open a path, not a database row.
        var prompt = adapter.LastContext!.Prompt;
        Assert.Contains("what is wrong here?", prompt);
        Assert.Equal(2, adapter.FilesVisibleDuringTurn.Count);
        foreach (var path in adapter.FilesVisibleDuringTurn)
        {
            Assert.Contains(path, prompt);
            Assert.True(Path.IsPathRooted(path));
        }
        Assert.Contains("image/png", prompt);

        // And once the turn is over the scratch tree holds none of them.
        Assert.Empty(Directory.GetFiles(Path.Combine(ScratchPath, "uploads")));
        Assert.Equal(2, _db.Context.ChatAttachments.Count());
    }

    [Fact]
    public async Task A_turn_with_no_attachments_says_nothing_about_them()
    {
        var (service, adapter, session) = await StartChatAsync();

        await service.ExecuteTurnAsync(session.Id, "hello", CancellationToken.None);

        Assert.DoesNotContain("[Attachments]", adapter.LastContext!.Prompt);
    }

    [Fact]
    public async Task The_transcript_keeps_the_attachments_so_a_reopened_chat_still_shows_them()
    {
        var (service, _, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);

        await RunTurnAsync(service, session.Id, "look", saved);

        var reopened = await service.GetByIdAsync("alice", session.Id);
        var userTurn = reopened!.Messages.Single(m => m.Role == "user");
        var attachment = Assert.Single(userTurn.Attachments!);
        Assert.Equal("sketch.png", attachment.FileName);
        Assert.Equal(saved![0].Id, attachment.Id);
    }

    [Fact]
    public async Task An_attachment_is_served_from_the_database_to_its_owner_and_nobody_else()
    {
        var (service, _, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);
        await RunTurnAsync(service, session.Id, "look", saved);
        var id = Guid.Parse(saved![0].Id);

        var found = await service.OpenAttachmentAsync("alice", session.Id, id);
        Assert.Equal("sketch.png", found!.Value.Meta.FileName);
        await using (var content = found.Value.Content)
        {
            Assert.Equal("pixels", await new StreamReader(content).ReadToEndAsync());
        }

        // Served with no file on disk at all — the download cannot be redirected
        // by anything the agent does, because it never consults the filesystem.
        Assert.Empty(Directory.GetFiles(Path.Combine(ScratchPath, "uploads")));

        Assert.Null(await service.OpenAttachmentAsync("mallory", session.Id, id));
        Assert.Null(await service.OpenAttachmentAsync("alice", session.Id, Guid.NewGuid()));
    }

    /// <summary>
    /// Materializing still writes into the scratch tree, whose parent is the
    /// agent's own working directory — and on POSIX the parent's write bit
    /// governs renaming the entry, so the agent can move the directory aside and
    /// leave a link. The exclusive create only refuses an existing final
    /// component; it walks a symlinked directory component happily.
    /// </summary>
    [Fact]
    public async Task A_turn_is_refused_when_the_uploads_directory_was_swapped_for_a_link()
    {
        var (service, _, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);

        var uploads = Path.Combine(ScratchPath, "uploads");
        var victim = Path.Combine(_scratchRoot, "victim");
        Directory.CreateDirectory(victim);
        Directory.Delete(uploads, recursive: true);
        Directory.CreateSymbolicLink(uploads, victim);

        await Assert.ThrowsAsync<IOException>(() => RunTurnAsync(service, session.Id, "look", saved));

        Assert.Empty(Directory.GetFileSystemEntries(victim));
    }

    [Fact]
    public async Task Deleting_the_chat_takes_its_attachments_with_it()
    {
        var (service, _, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);
        await RunTurnAsync(service, session.Id, "look", saved);

        await service.DeleteAsync("alice", session.Id);

        // The guarantee the whole decision rests on: the chat goes, the bytes go.
        Assert.Empty(_db.Context.ChatAttachments);
    }

    [Fact]
    public async Task An_upload_past_the_size_limit_is_refused_before_anything_is_stored()
    {
        var (service, _, session) = await StartChatAsync();
        var oversized = new UploadedFile(
            "huge.bin", "application/octet-stream",
            AttachmentIntake.MaxBytesPerFile + 1, new MemoryStream());

        await Assert.ThrowsAsync<AttachmentRejectedException>(
            () => service.SaveAttachmentsAsync("alice", session.Id, [Upload("fine.txt", "ok"), oversized]));

        Assert.Empty(_db.Context.ChatAttachments);
    }
}
