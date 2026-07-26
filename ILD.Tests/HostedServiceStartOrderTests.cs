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

        // A registration this probe could not identify shows up as a gap rather
        // than as an exception from an unrelated service — but if it is one of
        // the two being compared, the order is unverifiable and that has to
        // fail, not pass quietly.
        Assert.True(reconciler >= 0, Unidentified("RemoteWorkItemStartupReconciler", hosted));
        Assert.True(scheduler >= 0, Unidentified("WorkItemScheduler", hosted));
        Assert.True(reconciler < scheduler,
            $"The reconciler must start before the scheduler, but is registered at {reconciler} and the scheduler at {scheduler}");
    }

    private static string Unidentified(string name, IReadOnlyList<Type?> hosted)
        => $"{name} was not found among the {hosted.Count} IHostedService registrations "
           + $"([{string.Join(", ", hosted.Select(t => t?.Name ?? "<unidentified>"))}]). Either it is no longer "
           + "registered, or its factory could not be probed — see ImplementationTypeOf.";

    /// <summary>
    /// The concrete type a hosted-service registration yields. Factory
    /// registrations (<c>AddHostedService(sp =&gt; ...)</c>) name their type only
    /// inside the lambda, so run it against a provider that answers every
    /// request with an uninitialized instance — enough to see what comes back,
    /// without constructing anything or needing the real container.
    ///
    /// A factory that does more than resolve — touching what it gets back, or
    /// asking for an interface <c>GetUninitializedObject</c> cannot make —
    /// throws here. That is one unrelated registration's problem, so it yields
    /// an unknown type rather than failing the whole test: this is about the
    /// order of two specific services, and either of those going unidentified
    /// is caught by the assertions above.
    /// </summary>
    private static Type? ImplementationTypeOf(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is { } type) return type;
        if (descriptor.ImplementationInstance is { } instance) return instance.GetType();
        if (descriptor.ImplementationFactory is not { } factory) return null;

        try { return factory(new UninitializedProvider())?.GetType(); }
        catch { return null; }
    }

    private sealed class UninitializedProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType.IsAbstract || serviceType.IsInterface
                ? null
                : RuntimeHelpers.GetUninitializedObject(serviceType);
    }
}
