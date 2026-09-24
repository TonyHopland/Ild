using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class ManagedAgentProvisionerTests
{
    private static ManagedAgentProvisioner Create(IServiceProvider sp)
        => new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ManagedAgentProvisioner>.Instance);

    private static IServiceProvider BuildProvider(IManagedAgentService service, IProviderStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(service);
        services.AddSingleton(store ?? Mock.Of<IProviderStore>());
        return services.BuildServiceProvider();
    }

    private static ManagedAgentStatus Status(string key)
        => new(key, key, "pkg", "1.0.0", "1.0.0", false, null);

    [Fact]
    public async Task EnsureInstalledForProviderType_provisions_a_managed_agent_in_the_background()
    {
        var ensured = new ConcurrentBag<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = new Mock<IManagedAgentService>();
        svc.Setup(s => s.EnsureInstalledAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken _) =>
            {
                ensured.Add(key);
                done.TrySetResult();
                return Task.FromResult(Status(key));
            });

        using var sp = (ServiceProvider)BuildProvider(svc.Object);
        Create(sp).EnsureInstalledForProviderType("pi");

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["pi"], ensured.ToArray());
    }

    // A queued ensure is recorded here synchronously and only dropped once its
    // install ends, so with an install that never ends it stays for good.
    private static readonly System.Reflection.FieldInfo InFlightField =
        typeof(ManagedAgentProvisioner).GetField("_inFlight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    [Fact]
    public void EnsureInstalledForProviderType_ignores_unmanaged_provider_types()
    {
        var svc = new Mock<IManagedAgentService>();
        svc.Setup(s => s.EnsureInstalledAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<ManagedAgentStatus>().Task);

        using var sp = (ServiceProvider)BuildProvider(svc.Object);
        var provisioner = Create(sp);
        provisioner.EnsureInstalledForProviderType("openai");

        Assert.Empty((ConcurrentDictionary<string, byte>)InFlightField.GetValue(provisioner)!);
        svc.Verify(
            s => s.EnsureInstalledAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartAsync_provisions_every_managed_agent_used_by_existing_providers()
    {
        var ensured = new ConcurrentBag<string>();
        var bothSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = new Mock<IManagedAgentService>();
        svc.Setup(s => s.EnsureInstalledAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken _) =>
            {
                ensured.Add(key);
                if (ensured.Count >= 2) bothSeen.TrySetResult();
                return Task.FromResult(Status(key));
            });

        var store = new Mock<IProviderStore>();
        store.Setup(s => s.GetAllAiProvidersAsync()).ReturnsAsync(new List<AiProvider>
        {
            new() { Type = "pi" },
            new() { Type = "openai" },   // unmanaged — ignored
            new() { Type = "opencode" },
            new() { Type = "pi" },       // duplicate — provisioned once
        });

        using var sp = (ServiceProvider)BuildProvider(svc.Object, store.Object);
        await Create(sp).StartAsync(CancellationToken.None);

        await bothSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["opencode", "pi"], ensured.OrderBy(k => k).ToArray());
    }
}
