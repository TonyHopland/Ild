namespace ILD.Core.Services.Implementations;

/// <summary>What a start brings up: the whole profile, or one named service (and, on request, the services it references).</summary>
internal sealed record PreviewStartScope(string? ServiceName = null, bool StartDependencies = false)
{
    public static readonly PreviewStartScope Whole = new();

    public bool IsWhole => ServiceName == null;

    /// <summary>Whether a start of this scope, once it succeeds, has also done what <paramref name="other"/> asks for.</summary>
    public bool Covers(PreviewStartScope other)
        => IsWhole
           || (other.ServiceName != null
               && string.Equals(ServiceName, other.ServiceName, StringComparison.OrdinalIgnoreCase)
               && (StartDependencies || !other.StartDependencies));
}
