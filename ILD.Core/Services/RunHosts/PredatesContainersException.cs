namespace ILD.Core.Services.RunHosts;

/// <summary>A run or chat whose files are on ILD's own disk was asked to execute something; it was made before ILD ran everything in containers and is only read or deleted now.</summary>
public sealed class PredatesContainersException() : InvalidOperationException(Text)
{
    public const string Text = "This run or chat was made before ILD ran everything in containers. Its files are on ILD's own disk, so it can no longer run; start a new one.";

    /// <summary>Whether <paramref name="path"/>, of a run or chat, is on ILD's own disk under a host that runs everything in containers: it is read and deleted but never run again.</summary>
    public static bool Applies(IRunHost host, string? path)
        => host is RoutingRunHost routing && !string.IsNullOrEmpty(path) && !routing.HoldsPath(path);
}
