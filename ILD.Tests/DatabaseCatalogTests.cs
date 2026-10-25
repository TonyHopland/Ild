using ILD.Core.Services.Backup;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public sealed class DatabaseCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Callers_that_arrive_while_a_lookup_is_in_progress_share_it_and_a_finished_one_is_never_reused()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var client = new Mock<IWorkItemServerBackupClient>();
        client.Setup(c => c.GetInfoAsync(It.IsAny<WorkItemServerOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref asked);
                entered.TrySetResult();
                await release.Task;
                return null;
            });
        var resolver = new Mock<IWorkItemServerOptionsResolver>();
        resolver.Setup(r => r.ResolveForRepositoryAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemServerOptions { BaseUrl = "http://workitems", ApiKey = "k" });
        var services = new ServiceCollection();
        services.AddSingleton(client.Object);
        services.AddSingleton(resolver.Object);
        var catalog = new DatabaseCatalog(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseCatalog>.Instance);

        var first = catalog.DescribeAllAsync(Ct);
        await entered.Task.WaitAsync(Ct);
        var second = catalog.DescribeAllAsync(Ct);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, asked);
        Assert.Same(await first, await second);

        await catalog.DescribeAllAsync(Ct);
        Assert.Equal(2, asked);
    }
}
