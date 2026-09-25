using System.Collections;
using ILD.Core.Services.Implementations;

namespace ILD.Tests;

/// <summary>
/// A per-instance environment for the code under test to read and write in place
/// of the process one, so parallel tests cannot see each other's values. A name
/// not supplied reads as unset; it never falls through to the real environment.
/// </summary>
internal sealed class TestProcessEnvironment : IProcessEnvironment, IEnumerable<KeyValuePair<string, string?>>
{
    private readonly Dictionary<string, string?> _variables = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string? Get(string name)
    {
        lock (_gate)
            return _variables.TryGetValue(name, out var value) ? value : null;
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            if (value is null) _variables.Remove(name);
            else _variables[name] = value;
        }
    }

    public void Add(string name, string? value) => Set(name, value);

    /// <summary>
    /// Copy the real <c>PATH</c> in, for code that starts children needing
    /// <c>sh</c> or <c>node</c>. Only the copy is ever written.
    /// </summary>
    public TestProcessEnvironment WithRealPath()
    {
        Set("PATH", Environment.GetEnvironmentVariable("PATH"));
        return this;
    }

    public IEnumerator<KeyValuePair<string, string?>> GetEnumerator()
    {
        lock (_gate)
            return _variables.ToList().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
