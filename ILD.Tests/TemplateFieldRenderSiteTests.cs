using System.Text.Json;
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
/// The other half of the single-render invariant: templating still happens, and
/// it happens in the node executors.
///
/// "Prompts are templates" covers eight authored fields, not just the four
/// called "prompt" — a PR description, a PR comment, a Condition's output and a
/// Condition case's subject are template fields too, and the last of those
/// defaults to <c>{{Node.Input}}</c>, so a Condition that stopped rendering
/// would compare every TextMatches case against that literal string and quietly
/// fall through to its default edge. This file pins all eight sites so removing
/// rendering from the transport layer can never widen into removing it
/// everywhere.
/// </summary>
public class TemplateFieldRenderSiteTests
{
    private const string Title = "Fix the widget";

    /// <summary>
    /// Stand-in renderer resolving the two placeholders these tests exercise,
    /// which is enough to tell "the site rendered" from "the site did not".
    /// </summary>
    private sealed class FakeRendering : IPromptRenderingService
    {
        public Task<string> RenderAsync(string? template, Guid runId, WorkItemView workItem, string? previousNodeOutput)
            => Task.FromResult((template ?? string.Empty)
                .Replace("{{WorkItem.Title}}", workItem.Title)
                .Replace("{{Node.Input}}", previousNodeOutput ?? string.Empty));
    }

    private static WorkItemView Wi(Guid? repositoryId = null) => new()
    {
        Id = "WI-1",
        Title = Title,
        Description = "Body",
        RepositoryId = repositoryId,
    };

    private static LoopNode Node(NodeType type, object config) => new()
    {
        Id = Guid.NewGuid(),
        NodeType = type,
        Config = JsonSerializer.Serialize(config),
    };

    private static async Task<List<NodeOutcome>> RunAsync(INodeExecutor executor, LoopNode node, LoopRun run, IServiceProvider sp)
    {
        var outcomes = new List<NodeOutcome>();
        await foreach (var o in executor.ExecuteAsync(new NodeExecutionContext(run, node, sp, CancellationToken.None)))
            outcomes.Add(o);
        return outcomes;
    }

    private static ServiceCollection BaseServices(WorkItemView workItem)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(workItem);

        var services = new ServiceCollection();
        services.AddSingleton(workItems.Object);
        services.AddSingleton<IPromptRenderingService>(new FakeRendering());
        return services;
    }

    // ---- 1. AI node prompt -------------------------------------------------

    /// <summary>Adapter that records the prompt it was handed and returns a fixed reply.</summary>
    private sealed class CapturingAdapter : IAgentAdapter
    {
        public string? SeenPrompt { get; private set; }
        public string Name => "capture";
        public string[] SupportedProviderTypes => ["stub"];
        public ConfigFieldDescriptor[] ConfigSchema => [];

        public Task<NodeExecutionResult> ExecuteAsync(AgentExecutionContext context)
        {
            SeenPrompt = context.Prompt;
            return Task.FromResult(NodeExecutionResult.Ok("done"));
        }
    }

    [Fact]
    public async Task AI_node_renders_its_prompt_before_handing_it_to_the_adapter()
    {
        var adapter = new CapturingAdapter();
        var provider = new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "default",
            Type = "stub",
            IsDefault = true,
            Parallelism = 1,
            CreatedAt = DateTime.UtcNow,
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetDefaultAiProviderAsync()).ReturnsAsync(provider);

        var services = BaseServices(Wi());
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(Mock.Of<ILoopRunStore>());
        services.AddSingleton(Mock.Of<IAgentAdapterRegistry>(r =>
            r.ResolveForProvider(It.IsAny<AiProvider>()) == (Func<IAgentAdapter>)(() => adapter)));

        await RunAsync(
            new AINodeExecutor(),
            Node(NodeType.AI, new { prompt = "Work on {{WorkItem.Title}}" }),
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" },
            services.BuildServiceProvider());

        Assert.Equal($"Work on {Title}", adapter.SeenPrompt);
    }

    // ---- 2. Prompt node ----------------------------------------------------

    [Fact]
    public async Task Prompt_node_renders_its_template_into_its_output()
    {
        var outcomes = await RunAsync(
            new PromptNodeExecutor(),
            Node(NodeType.Prompt, new { prompt = "Compose for {{WorkItem.Title}}" }),
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" },
            BaseServices(Wi()).BuildServiceProvider());

        var success = Assert.IsType<NodeOutcome.Success>(outcomes.Last());
        Assert.Equal($"Compose for {Title}", success.Output);
    }

    // ---- 3. Human node prompt ---------------------------------------------

    [Fact]
    public async Task Human_node_renders_the_prompt_it_parks_on()
    {
        var outcomes = await RunAsync(
            new HumanNodeExecutor(),
            Node(NodeType.Human, new { prompt = "Approve {{WorkItem.Title}}?" }),
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" },
            BaseServices(Wi()).BuildServiceProvider());

        var waiting = Assert.IsType<NodeOutcome.WaitingAction>(outcomes.Last());
        Assert.Equal($"Approve {Title}?", waiting.Output);
    }

    // ---- 4-6. PR node: prompt, description template, comment template ------

    private static (ServiceCollection Services, Mock<IRemoteProvider> Remote, Repository Repo) PrServices(WorkItemView workItem)
    {
        var repo = new Repository
        {
            Id = workItem.RepositoryId!.Value,
            Name = "repo",
            CloneUrl = "https://example.com/owner/repo.git",
            DefaultBranch = "main",
            RemoteProviderId = Guid.NewGuid(),
        };
        var providerStore = new Mock<IProviderStore>();
        providerStore.Setup(s => s.GetRepositoryByIdAsync(repo.Id)).ReturnsAsync(repo);
        providerStore.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);

        var remote = new Mock<IRemoteProvider>();
        var services = BaseServices(workItem);
        services.AddSingleton(providerStore.Object);
        services.AddSingleton(remote.Object);
        services.AddSingleton(Mock.Of<IRepositoryManager>());
        return (services, remote, repo);
    }

    [Fact]
    public async Task PR_node_renders_its_prompt_when_announcing_the_node()
    {
        var wi = Wi(Guid.NewGuid());
        var (services, remote, _) = PrServices(wi);
        remote.Setup(r => r.CreatePullRequestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult("https://example.com/owner/repo/pull/7",
                "https://example.com/owner/repo/pull/7", RemotePrStatus.Open, null));

        var outcomes = await RunAsync(
            new PRNodeExecutor(),
            Node(NodeType.PR, new { prompt = "Opening a PR for {{WorkItem.Title}}" }),
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" },
            services.BuildServiceProvider());

        var starting = Assert.IsType<NodeOutcome.NodeStarting>(outcomes.First());
        Assert.Equal($"Opening a PR for {Title}", starting.EffectiveInput);
    }

    [Fact]
    public async Task PR_node_renders_the_description_template_into_the_pull_request_body()
    {
        var wi = Wi(Guid.NewGuid());
        var (services, remote, repo) = PrServices(wi);
        remote.Setup(r => r.CreatePullRequestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new RemotePrResult("https://example.com/owner/repo/pull/7",
                "https://example.com/owner/repo/pull/7", RemotePrStatus.Open, null));

        await RunAsync(
            new PRNodeExecutor(),
            Node(NodeType.PR, new { prDescriptionTemplate = "Closes {{WorkItem.Title}}" }),
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" },
            services.BuildServiceProvider());

        remote.Verify(r => r.CreatePullRequestAsync(
            repo.CloneUrl, It.IsAny<string>(), "main", Title, $"Closes {Title}"), Times.Once);
    }

    [Fact]
    public async Task PR_node_renders_the_comment_template_before_posting_it()
    {
        var wi = Wi(Guid.NewGuid());
        var (services, remote, repo) = PrServices(wi);
        remote.Setup(r => r.CreatePullRequestCommentAsync(repo.CloneUrl, "42", It.IsAny<string>()))
            .ReturnsAsync(true);

        await RunAsync(
            new PRNodeExecutor(),
            Node(NodeType.PR, new { prCommentTemplate = "Update on {{WorkItem.Title}}" }),
            new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                PrUrl = "https://example.com/owner/repo/pull/42",
            },
            services.BuildServiceProvider());

        remote.Verify(r => r.CreatePullRequestCommentAsync(repo.CloneUrl, "42", $"Update on {Title}"), Times.Once);
    }

    // ---- 7-8. Condition node: output and case subject ----------------------

    [Fact]
    public async Task Condition_node_renders_its_pass_through_output()
    {
        var outcomes = await RunAsync(
            new ConditionNodeExecutor(),
            Node(NodeType.Condition, new
            {
                output = "Reviewed {{WorkItem.Title}}",
                cases = new[] { new { variant = "TextMatches", pattern = "never-matches", edgeName = "Yes" } },
                defaultEdge = "No",
            }),
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" },
            BaseServices(Wi()).BuildServiceProvider());

        var success = Assert.IsType<NodeOutcome.Success>(outcomes.Last());
        Assert.Equal($"Reviewed {Title}", success.Output);
    }

    [Fact]
    public async Task Condition_case_renders_its_subject_before_matching_it()
    {
        var outcomes = await RunAsync(
            new ConditionNodeExecutor(),
            Node(NodeType.Condition, new
            {
                cases = new[]
                {
                    new { variant = "TextMatches", subject = "{{WorkItem.Title}}", pattern = "widget", edgeName = "Yes" },
                },
                defaultEdge = "No",
            }),
            new LoopRun { Id = Guid.NewGuid(), WorkItemId = "WI-1" },
            BaseServices(Wi()).BuildServiceProvider());

        // An unrendered subject would be the literal "{{WorkItem.Title}}", match
        // nothing, and silently take the default edge.
        var success = Assert.IsType<NodeOutcome.Success>(outcomes.Last());
        Assert.Equal("Yes", success.EdgeName);
    }

    [Fact]
    public async Task Condition_case_subject_defaults_to_the_node_input_and_is_rendered()
    {
        var outcomes = await RunAsync(
            new ConditionNodeExecutor(),
            Node(NodeType.Condition, new
            {
                cases = new[] { new { variant = "TextMatches", pattern = "approved", edgeName = "Yes" } },
                defaultEdge = "No",
            }),
            new LoopRun
            {
                Id = Guid.NewGuid(),
                WorkItemId = "WI-1",
                PreviousNodeOutput = "the reviewer approved it",
            },
            BaseServices(Wi()).BuildServiceProvider());

        var success = Assert.IsType<NodeOutcome.Success>(outcomes.Last());
        Assert.Equal("Yes", success.EdgeName);
    }
}
