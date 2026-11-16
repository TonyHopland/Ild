namespace ILD.Core.Services.Implementations;

/// <summary>Which services a single-service start has to bring up with it, and which it must refuse to start without.</summary>
internal static class PreviewDependencies
{
    /// <summary>
    /// Refuses to start a single service whose templates point at another service's
    /// port alias while that service is not up.
    ///
    /// <para>
    /// Such a reference resolves perfectly well — every profile service's port is
    /// allocated when the runtime is built, precisely so per-service starts can wire to
    /// each other — so without this the service launches holding a port number nothing
    /// is listening on, and then reports healthy, because its own health probe only
    /// asks about itself. Every surface a human checks agrees the preview is fine while
    /// the link between the two services is dead; the reference is also baked into the
    /// child's environment for as long as it runs, so the next start of the referenced
    /// service (a fresh ephemeral port, unless <c>suggestedPort</c> pins it) cannot
    /// repair it.
    /// </para>
    ///
    /// <para>
    /// Starting a whole profile does not come through here: that call launches every
    /// service itself, so each reference is live by the time it matters and the order
    /// services are declared in stays irrelevant.
    /// </para>
    /// </summary>
    public static void EnsureCrossReferencedServicesAreRunning(
        PreviewServiceConfig service,
        string profileName,
        PreviewProfileConfig profile,
        PreviewRuntime? runtime)
    {
        foreach (var alias in PreviewTemplates.CrossReferencedPortAliases(service))
        {
            var isLive = runtime?.Processes.Any(p =>
                string.Equals(p.Service.Port, alias, StringComparison.OrdinalIgnoreCase) && p.IsRunning) == true;
            if (isLive)
                continue;

            var owner = profile.Services.FirstOrDefault(s => string.Equals(s.Port, alias, StringComparison.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"Preview service '{service.Name}' references port alias '{alias}', but "
                + (owner == null
                    ? $"no service in profile '{profileName}' defines it."
                    : $"service '{owner.Name}' is not running. Start '{owner.Name}' first — starting "
                      + $"'{service.Name}' now would hand it a port nothing is listening on."));
        }
    }

    /// <summary>The names of the other services whose port aliases <paramref name="service"/> references.</summary>
    private static IReadOnlyList<string> DependsOnOf(PreviewServiceConfig service, IReadOnlyList<PreviewServiceConfig> services)
        => PreviewTemplates.CrossReferencedPortAliases(service)
            .Select(alias => services.FirstOrDefault(s => string.Equals(s.Port, alias, StringComparison.OrdinalIgnoreCase))?.Name)
            .OfType<string>()
            .Where(name => !string.Equals(name, service.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The services a single start has to launch, dependencies first and
    /// <paramref name="service"/> last: every port-alias owner it references (and,
    /// in turn, theirs) that is not running. A reference nothing in the profile owns
    /// is left to <see cref="EnsureCrossReferencedServicesAreRunning"/>, which names it.
    /// </summary>
    public static IReadOnlyList<PreviewServiceConfig> DependenciesToStart(
        PreviewServiceConfig service, string profileName, PreviewProfileConfig profile, PreviewRuntime? runtime)
    {
        var order = new List<PreviewServiceConfig>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { service.Name };

        void Visit(PreviewServiceConfig current)
        {
            foreach (var alias in PreviewTemplates.CrossReferencedPortAliases(current))
            {
                var isLive = runtime?.Processes.Any(p =>
                    string.Equals(p.Service.Port, alias, StringComparison.OrdinalIgnoreCase) && p.IsRunning) == true;
                if (isLive)
                    continue;

                var owner = profile.Services.FirstOrDefault(s => string.Equals(s.Port, alias, StringComparison.OrdinalIgnoreCase));
                if (owner == null)
                {
                    EnsureCrossReferencedServicesAreRunning(current, profileName, profile, runtime);
                    continue;
                }

                if (!visited.Add(owner.Name))
                    continue;

                Visit(owner);
                order.Add(owner);
            }
        }

        Visit(service);
        order.Add(service);
        return order;
    }
}
