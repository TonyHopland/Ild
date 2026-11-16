using System.Text.RegularExpressions;

namespace ILD.Core.Services.Implementations;

/// <summary>The <c>${...}</c> templates of a preview's config: which tokens they name and what they expand to for a runtime.</summary>
internal static class PreviewTemplates
{
    internal static readonly Regex TokenRegex = new("\\$\\{([^}]+)\\}", RegexOptions.Compiled);

    public static IEnumerable<string> Tokens(IEnumerable<string> templates)
        => templates.SelectMany(template => TokenRegex.Matches(template).Select(match => match.Groups[1].Value));

    /// <summary>Every field of a step or service that <see cref="Resolve"/> expands.</summary>
    public static IEnumerable<string> Templates(PreviewCommandConfig step)
    {
        IEnumerable<string?> fields = [step.Command, step.Cwd, .. step.Env?.Values ?? Enumerable.Empty<string>()];
        if (step is PreviewServiceConfig service)
            fields = fields.Append(service.HealthUrl).Append(service.PublicUrl);
        return fields.OfType<string>().Where(field => field.Length > 0);
    }

    public static string Resolve(string value, PreviewRuntime runtime, string? currentPortAlias)
    {
        return TokenRegex.Replace(value, match =>
        {
            var token = match.Groups[1].Value;
            if (string.Equals(token, "WORKTREE", StringComparison.OrdinalIgnoreCase))
                return runtime.WorktreePath;
            if (string.Equals(token, "STATE_DIR", StringComparison.OrdinalIgnoreCase))
                return runtime.StateDirectory;
            if (string.Equals(token, "HOST", StringComparison.OrdinalIgnoreCase))
                return "0.0.0.0";
            if (string.Equals(token, "PUBLIC_HOST", StringComparison.OrdinalIgnoreCase))
                return runtime.PublicHost;
            if (string.Equals(token, "PORT", StringComparison.OrdinalIgnoreCase))
            {
                if (currentPortAlias == null || !runtime.Ports.TryGetValue(currentPortAlias, out var currentPort))
                    throw new InvalidOperationException($"Template '{value}' references ${{PORT}} without a current service port.");
                return currentPort.ToString();
            }
            if (token.StartsWith("PORT:", StringComparison.OrdinalIgnoreCase))
            {
                var alias = token[5..];
                if (!runtime.Ports.TryGetValue(alias, out var namedPort))
                    throw new InvalidOperationException($"Template '{value}' references unknown port alias '{alias}'.");
                return namedPort.ToString();
            }
            if (PreviewVolumes.TryExpand(token, runtime.Volumes, value, out var folder))
                return folder;

            throw new InvalidOperationException($"Unsupported preview template token '{token}'.");
        });
    }

    public static string ResolveHealthUrl(PreviewServiceConfig service, PreviewRuntime runtime)
    {
        var resolved = Resolve(service.HealthUrl!, runtime, service.Port);
        if (!PreviewConfigLoader.IsOwnLoopbackHealthUrl(resolved))
            throw new InvalidOperationException(PreviewConfigLoader.HealthUrlNotLoopbackMessage(service.Name));
        return resolved;
    }

    public static string? ResolveOptional(string? value, PreviewRuntime runtime, string? currentPortAlias)
    {
        return string.IsNullOrWhiteSpace(value) ? null : Resolve(value, runtime, currentPortAlias);
    }

    /// <summary>The names of the other services whose port aliases <paramref name="service"/> references.</summary>
    public static IReadOnlyList<string> DependsOnOf(PreviewServiceConfig service, IReadOnlyList<PreviewServiceConfig> services)
        => CrossReferencedPortAliases(service)
            .Select(alias => services.FirstOrDefault(s => string.Equals(s.Port, alias, StringComparison.OrdinalIgnoreCase))?.Name)
            .OfType<string>()
            .Where(name => !string.Equals(name, service.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    /// <summary>
    /// The port aliases a service's templates name other than its own — its
    /// cross-service references, which are the ones that only mean anything while the
    /// service owning the alias is up. Reads the fields <see cref="PreviewLauncher.BuildResolvedStepAsync"/>
    /// and <see cref="ResolveHealthUrl"/> resolve for a launch, so a reference cannot
    /// hide in one this does not look at. Deliberately not <c>publicUrl</c>: it never
    /// reaches the child, and is resolved for display against a port map that always
    /// holds every alias — so a reference there is never handed to anything.
    /// </summary>
    public static IEnumerable<string> CrossReferencedPortAliases(PreviewServiceConfig service)
        => new[] { service.Command, service.Cwd, service.HealthUrl }
            .Concat(service.Env?.Values ?? Enumerable.Empty<string>())
            .Where(template => !string.IsNullOrEmpty(template))
            .SelectMany(template => TokenRegex.Matches(template!).Select(match => match.Groups[1].Value))
            .Where(token => token.StartsWith("PORT:", StringComparison.OrdinalIgnoreCase))
            .Select(token => token["PORT:".Length..])
            .Where(alias => !string.Equals(alias, service.Port, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
}
