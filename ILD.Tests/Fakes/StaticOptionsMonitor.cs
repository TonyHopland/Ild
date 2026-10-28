using Microsoft.Extensions.Options;

namespace ILD.Tests.Fakes;

/// <summary>Options that change only when a test sets them, and never notify.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private T _value = value;

    /// <summary>Runs once, on the next read of <see cref="CurrentValue"/>, then clears itself.</summary>
    public Action? OnNextRead;

    public T CurrentValue
    {
        get
        {
            Interlocked.Exchange(ref OnNextRead, null)?.Invoke();
            return _value;
        }
    }

    public T Get(string? name) => _value;

    public void Set(T value) => _value = value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
