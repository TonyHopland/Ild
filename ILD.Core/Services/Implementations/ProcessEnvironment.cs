namespace ILD.Core.Services.Implementations;

/// <summary>
/// The environment variables a service reads, and the one it writes back
/// (<see cref="WorktreePreviewService"/> appends to <c>PATH</c>). Production uses
/// <see cref="ProcessEnvironment.Current"/>; a test supplies its own instance so
/// parallel tests never share values through the process.
/// </summary>
public interface IProcessEnvironment
{
    string? Get(string name);

    void Set(string name, string? value);
}

/// <summary>The real process environment.</summary>
public sealed class ProcessEnvironment : IProcessEnvironment
{
    public static readonly ProcessEnvironment Current = new();

    private ProcessEnvironment()
    {
    }

    public string? Get(string name) => Environment.GetEnvironmentVariable(name);

    public void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);
}
