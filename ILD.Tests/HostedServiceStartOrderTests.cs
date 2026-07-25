using System.Runtime.CompilerServices;
using ILD.Api.Configuration;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ILD.Tests;

public class HostedServiceStartOrderTests
{
    /// <summary>
    /// The reconciler settles every local run against the server — cancelling
    /// the ones whose item has been finished, reset, or deleted — inside its
    /// StartAsync. Hosted services start in registration order, one awaited
    /// after the next, so registering it first is what stops the scheduler's
    /// opening pass from deriving its Active Work Item Set out of runs that are
    /// about to be cancelled: heartbeating them, and re-claiming an item the
    /// server has already handed to someone else.
    ///
    /// A comment cannot enforce that. An alphabetical sort or a merge that moves
    /// either line silently reopens the window, and nothing else would fail.
    /// </summary>
    [Fact]
    public void Startup_reconciler_is_registered_before_the_scheduler()
    {
        var services = new ServiceCollection().AddIldServices();

        var hosted = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(ImplementationTypeOf)
            .ToList();

        var reconciler = hosted.IndexOf(typeof(RemoteWorkItemStartupReconciler));
        var scheduler = hosted.IndexOf(typeof(WorkItemScheduler));

        Assert.True(reconciler >= 0, "RemoteWorkItemStartupReconciler is not registered as a hosted service");
        Assert.True(scheduler >= 0, "WorkItemScheduler is not registered as a hosted service");
        Assert.True(reconciler < scheduler,
            $"The reconciler must start before the scheduler, but is registered at {reconciler} and the scheduler at {scheduler}");
    }

    /// <summary>
    /// The concrete type a hosted-service registration yields. Factory
    /// registrations (<c>AddHostedService(sp =&gt; ...)</c>) name their type only
    /// inside the lambda, so run it against a provider that answers every
    /// request with an uninitialized instance — enough to see what comes back,
    /// without constructing anything or needing the real container.
    /// </summary>
    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
        => descriptor.ImplementationType
           ?? descriptor.ImplementationInstance?.GetType()
           ?? descriptor.ImplementationFactory?.Invoke(new UninitializedProvider())?.GetType();

    private sealed class UninitializedProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => RuntimeHelpers.GetUninitializedObject(serviceType);
    }
}
