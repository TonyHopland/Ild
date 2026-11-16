using System.Collections.Concurrent;
using ILD.Core.Services.Implementations;

namespace ILD.Tests.Fakes;

/// <summary>
/// A health probe a test scripts by service name: every service is healthy at once unless it is held (until released
/// or cancelled) or made to fail, and <see cref="ReachedAsync"/> says when a service's wait has begun.
/// </summary>
internal sealed class FakePreviewHealthProbe : IPreviewHealthProbe
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _reached = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The services waited for, in the order their waits began.</summary>
    public List<string> Probed { get; } = [];

    /// <summary>Keeps <paramref name="service"/> unhealthy until <see cref="Release"/>.</summary>
    public void Hold(string service) => _holds[service] = NewSignal();

    public void Release(string service)
    {
        if (_holds.TryGetValue(service, out var hold))
            hold.TrySetResult();
    }

    /// <summary>Makes <paramref name="service"/> fail its wait with <paramref name="message"/>.</summary>
    public void Fail(string service, string message) => _failures[service] = message;

    public Task ReachedAsync(string service) => _reached.GetOrAdd(service, _ => NewSignal()).Task.WaitAsync(TimeSpan.FromSeconds(30));

    public async Task WaitForHealthAsync(
        string serviceName, string healthUrl, string worktreePath, int timeoutSeconds, ManagedPreviewProcess? watched, CancellationToken cancellationToken)
    {
        lock (Probed) Probed.Add(serviceName);
        _reached.GetOrAdd(serviceName, _ => NewSignal()).TrySetResult();
        if (_holds.TryGetValue(serviceName, out var hold))
            await hold.Task.WaitAsync(cancellationToken);
        if (_failures.TryGetValue(serviceName, out var message))
            throw new InvalidOperationException(message);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
