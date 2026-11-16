using ILD.Core.Services.RunHosts.Local;

namespace ILD.Core.Services.RunHosts;

/// <summary>
/// The one run host, and what only some backends can be asked: made once, where the backend is chosen, so no caller finds out what the host is by
/// testing its type. <see cref="Host"/> and <see cref="Operator"/> are always there; the others are null on a backend that does not have them.
/// </summary>
public sealed record RunHostSelection(
    IRunHost Host,
    IRunHostOperator Operator,
    IRunSweep? Sweep,
    ISignInTerminalHost? SignIn,
    ILocalTerminalCommands? LocalTerminalCommands)
{
    /// <summary>Runs on ILD's own machine: nothing to sweep, and a sign-in terminal that is started with a routed command line.</summary>
    public static RunHostSelection For(LocalRunHost local) => new(local, local, null, null, local);

    /// <summary>Runs in containers or pods: the sweep and the sign-in container are the isolated host's.</summary>
    public static RunHostSelection For(RoutingRunHost routing) => new(routing, routing, routing, routing, null);
}
