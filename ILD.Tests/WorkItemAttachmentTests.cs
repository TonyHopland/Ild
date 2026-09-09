using System.Text;
using ILD.Core.Services.Attachments;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A file attached to a work item, from the WorkItem server that holds it
/// (ADR-0001) through to the prompt the agent of a run actually receives. The
/// bytes have to cross a process boundary and land somewhere the agent uid can
/// read but git will not see, so the path is covered end to end against a real
/// <c>WorkItemService</c> rather than a mock of it.
/// </summary>
public sealed class WorkItemAttachmentTests : IDisposable
{
    private readonly FakeWorkItemServerHarness _server = new();
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(), "ild-wi-attachment-tests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousScratchRoot =
        Environment.GetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar);

    public WorkItemAttachmentTests()
        => Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, _scratchRoot);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, _previousScratchRoot);
        _server.Dispose();
        try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, recursive: true); }
        catch (IOException) { }
    }

    private static readonly WorkItemServerOptions Opts = new() { BaseUrl = "http://localhost", ApiKey = "k" };

    private async Task<RemoteWorkItem> CreateItemAsync()
        => await _server.Client.CreateAsync(Opts, new RemoteCreateWorkItemRequest { Title = "WI" });

    private async Task<RemoteWorkItemAttachment> AttachAsync(string id, string name, string content, string? type = null)
        => (await _server.Client.AddAttachmentAsync(
            Opts, id, name, type, new MemoryStream(Encoding.UTF8.GetBytes(content))))!;

    [Fact]
    public async Task An_attachment_round_trips_through_the_work_item_server()
    {
        var item = await CreateItemAsync();

        var attachment = await AttachAsync(item.Id, "sketch.png", "pixels", "image/png");

        Assert.Equal("sketch.png", attachment.FileName);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(6, attachment.SizeBytes);

        var reread = await _server.Client.GetAsync(Opts, item.Id);
        Assert.Equal([attachment.Id], reread!.Attachments.Select(a => a.Id));

        var content = await _server.Client.GetAttachmentAsync(Opts, item.Id, attachment.Id);
        Assert.Equal("pixels", Encoding.UTF8.GetString(content!.Bytes));
    }

    [Fact]
    public async Task The_server_stores_a_hostile_file_name_as_one_harmless_segment()
    {
        var item = await CreateItemAsync();

        var attachment = await AttachAsync(item.Id, "../../etc/passwd", "root:x:0:0");

        Assert.Equal("passwd", attachment.FileName);
    }

    [Fact]
    public async Task Detaching_removes_the_metadata_and_the_bytes()
    {
        var item = await CreateItemAsync();
        var attachment = await AttachAsync(item.Id, "sketch.png", "pixels");

        Assert.True(await _server.Client.DeleteAttachmentAsync(Opts, item.Id, attachment.Id));

        var reread = await _server.Client.GetAsync(Opts, item.Id);
        Assert.Empty(reread!.Attachments);
        Assert.Null(await _server.Client.GetAttachmentAsync(Opts, item.Id, attachment.Id));
        Assert.False(await _server.Client.DeleteAttachmentAsync(Opts, item.Id, attachment.Id));
    }

    // ── materialization into a run ───────────────────────────────────────────

    private async Task<(WorkItemView View, IWorkItemAttachmentMaterializer Materializer)> ItemWithAttachmentsAsync(
        params (string Name, string Content)[] files)
    {
        var item = await CreateItemAsync();
        foreach (var (name, content) in files)
            await AttachAsync(item.Id, name, content);

        var remote = (await _server.Client.GetAsync(Opts, item.Id))!;
        var view = new WorkItemView { Id = item.Id, Title = "WI", Attachments = remote.Attachments };

        var manager = new Mock<IWorkItemManager>();
        manager.Setup(m => m.GetAttachmentAsync(item.Id, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, string attachmentId, CancellationToken ct) =>
                _server.Client.GetAttachmentAsync(Opts, id, attachmentId, ct));

        return (view, new WorkItemAttachmentMaterializer(manager.Object));
    }

    [Fact]
    public async Task Materializing_writes_the_files_outside_the_worktree_under_their_own_names()
    {
        var (view, materializer) = await ItemWithAttachmentsAsync(("sketch.png", "pixels"), ("run.log", "boom"));
        var runId = Guid.NewGuid();

        var local = await materializer.EnsureLocalAsync(view, runId);

        Assert.Equal(2, local.Files.Count);
        Assert.StartsWith(_scratchRoot, local.Directory!);
        foreach (var file in local.Files)
        {
            Assert.True(Path.IsPathRooted(file.StoredPath));
            Assert.StartsWith(local.Directory!, file.StoredPath);
            Assert.EndsWith(file.FileName, file.StoredPath);
        }
        Assert.Equal("pixels", await File.ReadAllTextAsync(local.Files[0].StoredPath));
        Assert.Equal("boom", await File.ReadAllTextAsync(local.Files[1].StoredPath));
    }

    [Fact]
    public async Task Two_attachments_sharing_a_name_stay_distinct_on_disk()
    {
        var (view, materializer) = await ItemWithAttachmentsAsync(("shot.png", "first"), ("shot.png", "second"));

        var local = await materializer.EnsureLocalAsync(view, Guid.NewGuid());

        Assert.Equal(2, local.Files.Count);
        Assert.NotEqual(local.Files[0].StoredPath, local.Files[1].StoredPath);
        Assert.Equal("first", await File.ReadAllTextAsync(local.Files[0].StoredPath));
        Assert.Equal("second", await File.ReadAllTextAsync(local.Files[1].StoredPath));
    }

    [Fact]
    public async Task Materializing_again_in_the_same_run_reuses_what_is_already_there()
    {
        var (view, materializer) = await ItemWithAttachmentsAsync(("sketch.png", "pixels"));
        var runId = Guid.NewGuid();

        var first = await materializer.EnsureLocalAsync(view, runId);
        var writtenAt = File.GetLastWriteTimeUtc(first.Files[0].StoredPath);

        var second = await materializer.EnsureLocalAsync(view, runId);

        Assert.Equal(first.Files[0].StoredPath, second.Files[0].StoredPath);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(second.Files[0].StoredPath));
    }

    [Fact]
    public async Task An_item_with_no_attachments_materializes_nothing()
    {
        var (view, materializer) = await ItemWithAttachmentsAsync();

        var local = await materializer.EnsureLocalAsync(view, Guid.NewGuid());

        Assert.Null(local.Directory);
        Assert.Empty(local.Files);
    }

    // ── what the AI node's agent actually receives ──────────────────────────

    private sealed class CapturingAdapter : IAgentAdapter
    {
        public AgentExecutionContext? LastContext { get; private set; }
        public string Name => "stub";
        public string[] SupportedProviderTypes => ["stub"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            LastContext = context;
            return Task.FromResult(NodeExecutionResult.Ok("done"));
        }
    }

    private async Task<(CapturingAdapter Adapter, MaterializedAttachments Local)> RunAiNodeAsync(
        string prompt, params (string Name, string Content)[] files)
    {
        var (view, materializer) = await ItemWithAttachmentsAsync(files);
        var run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = view.Id, WorktreePath = "/worktrees/wi" };

        var provider = new AiProvider
        {
            Id = Guid.NewGuid(), Name = "default", Type = "stub", IsDefault = true,
            Parallelism = 1, CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);

        var adapter = new CapturingAdapter();
        var services = new ServiceCollection();
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(Mock.Of<ILoopRunStore>());
        services.AddSingleton(Mock.Of<IWorkItemManager>(m =>
            m.GetWorkItemAsync(It.IsAny<string>()) == Task.FromResult<WorkItemView?>(view)));
        services.AddSingleton(Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter)));
        services.AddSingleton(materializer);
        var sp = services.BuildServiceProvider();

        var node = new LoopNode
        {
            Id = Guid.NewGuid(),
            NodeType = NodeType.AI,
            Config = System.Text.Json.JsonSerializer.Serialize(new { prompt }),
        };

        var executor = new AINodeExecutor();
        await foreach (var _ in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None))) { }

        return (adapter, await materializer.EnsureLocalAsync(view, run.Id));
    }

    [Fact]
    public async Task An_AI_node_hands_the_agent_the_attachment_paths_and_the_directory_holding_them()
    {
        var (adapter, local) = await RunAiNodeAsync("Fix the bug.", ("sketch.png", "pixels"));

        var ctx = adapter.LastContext!;
        Assert.Contains("Fix the bug.", ctx.Prompt);
        Assert.Contains(local.Files[0].StoredPath, ctx.Prompt);
        // The files sit outside the run's worktree, so the directory has to be
        // granted explicitly or the agent cannot open what it was just told about.
        Assert.Contains(local.Directory!, ctx.AdditionalAllowedDirectories!);
    }

    [Fact]
    public async Task An_AI_node_on_an_item_with_no_attachments_is_unchanged()
    {
        var (adapter, _) = await RunAiNodeAsync("Fix the bug.");

        Assert.Equal("Fix the bug.", adapter.LastContext!.Prompt);
        Assert.Null(adapter.LastContext.AdditionalAllowedDirectories);
    }

    [Fact]
    public async Task A_prompt_that_places_the_placeholder_is_not_also_given_the_appended_block()
    {
        var (adapter, _) = await RunAiNodeAsync(
            "Look at {{WorkItem.Attachments}} and fix it.", ("sketch.png", "pixels"));

        // The renderer is not wired into this executor-only harness, so the token
        // survives; what matters is that the executor left the author's layout
        // alone rather than appending a second copy of the list.
        Assert.DoesNotContain("[Attachments]", adapter.LastContext!.Prompt);
    }

    [Fact]
    public async Task The_placeholder_renders_the_same_block_the_executor_would_have_appended()
    {
        var (view, materializer) = await ItemWithAttachmentsAsync(("sketch.png", "pixels"));
        var local = await materializer.EnsureLocalAsync(view, Guid.NewGuid());

        var rendered = new PromptTemplateResolver().Render(
            "Before {{WorkItem.Attachments}} after",
            new PromptContext(WorkItemAttachments: local.Files));

        Assert.Contains("[Attachments]", rendered);
        Assert.Contains(local.Files[0].StoredPath, rendered);
        Assert.StartsWith("Before ", rendered);
        Assert.EndsWith(" after", rendered);
    }

    [Fact]
    public void The_placeholder_renders_empty_for_an_item_with_no_attachments()
    {
        var rendered = new PromptTemplateResolver().Render("A{{WorkItem.Attachments}}B", new PromptContext());

        Assert.Equal("AB", rendered);
    }
}
