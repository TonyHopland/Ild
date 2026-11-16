namespace ILD.Core.Services.RunHosts.Isolated;

/// <summary>What a leftover is, so deleting it does not re-read a name.</summary>
public abstract record LeftoverTarget
{
    /// <summary>What a run left: its container or pod, volume or claim, network, policies.</summary>
    public sealed record Run(Guid Id) : LeftoverTarget;

    /// <summary>A sign-in or toolchain installer ILD no longer has open, with what it was given (an installer's network or policy).</summary>
    public sealed record SideEnvironment(string Name, bool IsInstaller) : LeftoverTarget;

    /// <summary>The network or NetworkPolicy of a direct route (network mode Off) nothing uses any more.</summary>
    public sealed record OpenRoute(string Name) : LeftoverTarget;
}

/// <summary>One object an engine found that belongs to a sign-in or an installer: its name and the role it was labelled with.</summary>
public readonly record struct SideEnvironmentFact(string Name, string? Role);

/// <summary>An open route an engine found, and whether nothing is on it: no container attached, or the pod it was for is gone.</summary>
public readonly record struct OpenRouteFact(string Name, bool Unused);

/// <summary>What an engine found of ILD's objects, with nothing decided: the sweep policy decides what is a leftover.</summary>
public sealed record SweepFacts(
    IReadOnlyCollection<string> RunIds, IReadOnlyCollection<SideEnvironmentFact> SideEnvironments, IReadOnlyCollection<OpenRouteFact> OpenRoutes)
{
    public static readonly SweepFacts None = new([], [], []);
}

/// <summary>
/// What counts as a leftover, for either engine (ADR-0034): a run's objects once the run is neither live nor retained, a sign-in or
/// installer ILD does not have open, a direct route nothing uses. An engine only finds the objects and deletes what it is handed.
/// </summary>
public static class RunSweepPolicy
{
    public static IReadOnlyList<RunLeftover> Leftovers(
        string kind, SweepFacts facts, IReadOnlySet<Guid> liveIds, IReadOnlySet<Guid> retained, IReadOnlySet<string> openSideEnvironments)
    {
        var leftovers = new List<RunLeftover>();
        foreach (var run in facts.RunIds.Distinct(StringComparer.Ordinal))
        {
            // An id that is not one is still a leftover, which nothing can delete: ILD did not write it.
            if (!Guid.TryParseExact(run, "N", out var id)) leftovers.Add(new RunLeftover(kind, run));
            else if (!liveIds.Contains(id) && !retained.Contains(id)) leftovers.Add(new RunLeftover(kind, run) { Target = new LeftoverTarget.Run(id) });
        }
        foreach (var side in facts.SideEnvironments)
            if (side.Role is RunPaths.SignInRole or RunPaths.ToolsRole && !openSideEnvironments.Contains(side.Name)
                && leftovers.All(l => l.Location != side.Name))
                leftovers.Add(new RunLeftover(kind, side.Name) { Target = new LeftoverTarget.SideEnvironment(side.Name, side.Role == RunPaths.ToolsRole) });
        foreach (var route in facts.OpenRoutes)
            if (route.Unused && leftovers.All(l => l.Location != route.Name))
                leftovers.Add(new RunLeftover(kind, route.Name) { Target = new LeftoverTarget.OpenRoute(route.Name) });
        return leftovers;
    }

    /// <summary>
    /// What <paramref name="leftover"/> is: the target it was found with, or, for one made from a location alone, what the location
    /// names. Null for a location that names nothing ILD made.
    /// </summary>
    public static LeftoverTarget? TargetOf(RunLeftover leftover, Func<string, bool> isOpenRoute)
    {
        if (leftover.Target is { } target) return target;
        var location = leftover.Location;
        if (location.StartsWith(RunNames.SignInPrefix, StringComparison.Ordinal))
            return new LeftoverTarget.SideEnvironment(location, IsInstaller: false);
        if (location.StartsWith(RunNames.InstallerPrefix, StringComparison.Ordinal))
            return new LeftoverTarget.SideEnvironment(location, IsInstaller: true);
        if (isOpenRoute(location)) return new LeftoverTarget.OpenRoute(location);
        return Guid.TryParseExact(location, "N", out var id) ? new LeftoverTarget.Run(id) : null;
    }
}
