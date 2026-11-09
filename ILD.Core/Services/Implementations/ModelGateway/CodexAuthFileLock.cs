namespace ILD.Core.Services.Implementations.ModelGateway;

/// <summary>Held only around a short read-compare-write of <c>~/.codex/auth.json</c>, never across HTTP.</summary>
internal static class CodexAuthFileLock
{
    internal static readonly object Gate = new();
}
