using Microsoft.Extensions.Options;

namespace ILD.Tests.Fakes;

/// <summary>Options that hold the value they were given and never notify of a change.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    /// <summary>Runs once, on the next read of <see cref="CurrentValue"/>, then clears itself.</summary>
    public Action? OnNextRead;

    public T CurrentValue
    {
        get
        {
            Interlocked.Exchange(ref OnNextRead, null)?.Invoke();
            return value;
        }
    }

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
