using System.Text;
using ILD.Core.Services.Attachments;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Moq;

namespace ILD.Tests;

/// <summary>
/// Attaching a file to a chat turn, end to end through the service: the bytes
/// land in the session's own scratch directory (the agent's working directory,
/// and the one tree ADR-0014 already makes readable by the agent uid), and the
/// agent is told where they are — the whole feature, since adapters pass the
/// prompt through unchanged and cannot be handed a blob.
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

    private sealed class CapturingAdapter : IAgentAdapter
    {
        public AgentExecutionContext? LastContext { get; private set; }
        public string Name => "fake";
        public string[] SupportedProviderTypes => ["fake"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            LastContext = context;
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

    [Fact]
    public async Task An_attached_file_lands_in_the_session_uploads_directory()
    {
        var (service, _, session) = await StartChatAsync();

        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);

        var attachment = Assert.Single(saved!);
        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        Assert.Equal(Path.Combine(scratchPath, "uploads", "sketch.png"), attachment.StoredPath);
        Assert.Equal("pixels", await File.ReadAllTextAsync(attachment.StoredPath));
    }

    [Fact]
    public async Task Saving_attachments_for_another_users_chat_stores_nothing()
    {
        var (service, _, session) = await StartChatAsync();

        var saved = await service.SaveAttachmentsAsync("mallory", session.Id, [Upload("sketch.png", "pixels")]);

        Assert.Null(saved);
        var scratchPath = _db.Context.ChatSessions.Single().ScratchPath;
        Assert.False(Directory.Exists(Path.Combine(scratchPath, "uploads")));
    }

    [Fact]
    public async Task The_agent_prompt_carries_the_absolute_path_of_every_attachment()
    {
        var (service, adapter, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync(
            "alice", session.Id, [Upload("sketch.png", "pixels", "image/png"), Upload("run.log", "boom")]);

        await service.ExecuteTurnAsync(
            session.Id, "what is wrong here?", openWorkItemId: null, openLoopDocument: null, saved, CancellationToken.None);

        var prompt = adapter.LastContext!.Prompt;
        Assert.Contains("what is wrong here?", prompt);
        foreach (var attachment in saved!)
        {
            Assert.Contains(attachment.StoredPath, prompt);
            Assert.Contains(attachment.FileName, prompt);
            Assert.True(Path.IsPathRooted(attachment.StoredPath));
        }
        Assert.Contains("image/png", prompt);
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

        await service.ExecuteTurnAsync(
            session.Id, "look", openWorkItemId: null, openLoopDocument: null, saved, CancellationToken.None);

        var reopened = await service.GetByIdAsync("alice", session.Id);
        var userTurn = reopened!.Messages.Single(m => m.Role == "user");
        var attachment = Assert.Single(userTurn.Attachments!);
        Assert.Equal("sketch.png", attachment.FileName);
        Assert.Equal(saved![0].Id, attachment.Id);
    }

    [Fact]
    public async Task An_attachment_can_be_fetched_back_by_its_owner_and_nobody_else()
    {
        var (service, _, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);
        await service.ExecuteTurnAsync(
            session.Id, "look", openWorkItemId: null, openLoopDocument: null, saved, CancellationToken.None);

        var found = await service.FindAttachmentAsync("alice", session.Id, saved![0].Id);
        Assert.Equal("sketch.png", found!.FileName);

        Assert.Null(await service.FindAttachmentAsync("mallory", session.Id, saved[0].Id));
        Assert.Null(await service.FindAttachmentAsync("alice", session.Id, "no-such-id"));
    }

    [Fact]
    public async Task Deleting_the_chat_takes_its_uploads_with_it()
    {
        var (service, _, session) = await StartChatAsync();
        var saved = await service.SaveAttachmentsAsync("alice", session.Id, [Upload("sketch.png", "pixels")]);

        await service.DeleteAsync("alice", session.Id);

        Assert.False(File.Exists(saved![0].StoredPath));
    }
}
