using ILD.Core.Services.Implementations.Executors;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ILD.Tests.Fakes;

/// <summary>
/// A PR node with everything it reads around it: one work item, its repository and the run.
/// Tests change what they care about on the exposed mocks and entities, then <see cref="RunAsync"/>.
/// </summary>
internal sealed class PrNodeBuilder
{
    public WorkItemView WorkItem { get; }
    public Repository Repository { get; }
    public Mock<IWorkItemManager> WorkItems { get; } = new();
    public Mock<IProviderStore> Providers { get; } = new();
    public Mock<IRemoteProvider> Remote { get; }
    public Mock<IRepositoryManager> RepositoryManager { get; } = new();
    public LoopRun Run { get; }
    public LoopNode Node { get; }

    /// <summary>Registered with the node's services only when set.</summary>
    public ILoopRunStore? Runs { get; set; }
    public IRunNotifier? Notifier { get; set; }
    public IEventLogService? Events { get; set; }
    public IPromptRenderingService? Rendering { get; set; }

    public PrNodeBuilder(bool strictRemote = false, string cloneUrl = "https://example.com/o/r.git")
    {
        Remote = new Mock<IRemoteProvider>(strictRemote ? MockBehavior.Strict : MockBehavior.Default);
        var repoId = Guid.NewGuid();
        WorkItem = new WorkItemView { Id = "WI-1", Title = "T", Description = "D", RepositoryId = repoId };
        Repository = new Repository
        {
            Id = repoId, Name = "r", CloneUrl = cloneUrl, DefaultBranch = "main", RemoteProviderId = Guid.NewGuid(),
        };
        WorkItems.Setup(m => m.GetWorkItemAsync(It.IsAny<string>())).ReturnsAsync(WorkItem);
        Providers.Setup(s => s.GetRepositoryByIdAsync(repoId)).ReturnsAsync(Repository);
        Providers.Setup(s => s.GetRemoteProviderByIdAsync(It.IsAny<Guid>())).ReturnsAsync((RemoteProvider?)null);
        Run = new LoopRun { Id = Guid.NewGuid(), WorkItemId = WorkItem.Id };
        Node = new LoopNode { Id = Guid.NewGuid(), NodeType = NodeType.PR, Config = "{}" };
    }

    public IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(WorkItems.Object);
        services.AddSingleton(Providers.Object);
        services.AddSingleton(Remote.Object);
        services.AddSingleton(RepositoryManager.Object);
        if (Runs is not null) services.AddSingleton(Runs);
        if (Notifier is not null) services.AddSingleton(Notifier);
        if (Events is not null) services.AddSingleton(Events);
        if (Rendering is not null) services.AddSingleton(Rendering);
        return services.BuildServiceProvider();
    }

    public async Task<List<NodeOutcome>> RunAsync()
    {
        var outcomes = new List<NodeOutcome>();
        await foreach (var outcome in new PRNodeExecutor()
            .ExecuteAsync(new NodeExecutionContext(Run, Node, Services(), CancellationToken.None)))
            outcomes.Add(outcome);
        return outcomes;
    }
}
