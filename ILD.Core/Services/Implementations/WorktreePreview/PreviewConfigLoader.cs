using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations;

/// <summary>
/// Reads a worktree's <c>ild.config.json</c> and answers with a validated <see cref="PreviewProfile"/>:
/// the one place a preview's config is loaded, selected, validated and edited. It holds no state of its own.
/// </summary>
internal sealed class PreviewConfigLoader(IRunFiles files)
{
    internal const string ConfigFileName = "ild.config.json";

    /// <summary>Health wait for a service that sets no <c>healthTimeoutSeconds</c>; generous so a slow first build is not reported as a failure.</summary>
    internal const int DefaultHealthTimeoutSeconds = 300;

    /// <summary>The largest <c>healthTimeoutSeconds</c> a service may set.</summary>
    internal const int MaxHealthTimeoutSeconds = 3600;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task<LoadedPreviewConfig> LoadAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(worktreePath, ConfigFileName);
        await using var stream = await files.OpenFileForReadAsync(configPath, cancellationToken);
        if (stream is null)
        {
            return new LoadedPreviewConfig(false, worktreePath, configPath, null, $"No {ConfigFileName} found in worktree root.");
        }

        var config = await JsonSerializer.DeserializeAsync<IldWorkspaceConfig>(stream, JsonOptions, cancellationToken);
        if (config?.Preview?.Profiles == null || config.Preview.Profiles.Count == 0)
        {
            return new LoadedPreviewConfig(false, worktreePath, configPath, null, "ild.config.json does not define any preview profiles.");
        }

        return new LoadedPreviewConfig(true, worktreePath, configPath, config, null);
    }

    public static LoadedPreviewConfig RequireConfigured(LoadedPreviewConfig loaded)
        => loaded.Configured && loaded.Config != null
            ? loaded
            : throw new InvalidOperationException(loaded.Message ?? "No ild.config.json preview profile found.");

    /// <summary>The profile <paramref name="requestedProfile"/> names (or the default one), found and validated.</summary>
    public static PreviewProfile Resolve(LoadedPreviewConfig loaded, string? requestedProfile)
    {
        var profile = Find(loaded, requestedProfile);
        ValidateProfile(profile.Name, profile.Config);
        return profile;
    }

    /// <summary>The profile <paramref name="requestedProfile"/> names (or the default one), found but not validated.</summary>
    public static PreviewProfile Find(LoadedPreviewConfig loaded, string? requestedProfile)
    {
        var name = SelectProfileName(loaded.Config!, requestedProfile);
        if (!loaded.Config!.Preview!.Profiles.TryGetValue(name, out var profile) || profile == null)
            throw new InvalidOperationException($"Preview profile '{name}' not found.");

        return new PreviewProfile(loaded, name, profile);
    }

    public static string SelectProfileName(IldWorkspaceConfig config, string? requestedProfile)
    {
        if (!string.IsNullOrWhiteSpace(requestedProfile))
            return requestedProfile.Trim();

        if (!string.IsNullOrWhiteSpace(config.Preview?.DefaultProfile))
            return config.Preview.DefaultProfile.Trim();

        return config.Preview!.Profiles.Keys.First();
    }

    /// <summary>One service's entry as written in the config, or null when it has none; the raw JSON keeps fields the model does not surface.</summary>
    public async Task<string?> ReadServiceConfigAsync(string worktreePath, string serviceName, string? requestedProfile, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(worktreePath, cancellationToken);
        if (!loaded.Configured || loaded.Config == null)
            return null;

        var node = await LoadNodeAsync(loaded.ConfigPath, cancellationToken);
        var serviceNode = FindServiceNode(node, SelectProfileName(loaded.Config, requestedProfile), serviceName);
        return serviceNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Replaces one service's entry in place, after the edited service passes the rules a start applies.</summary>
    public async Task WriteServiceConfigAsync(
        string worktreePath, string serviceName, string serviceConfigJson, string? requestedProfile, CancellationToken cancellationToken)
    {
        var loaded = RequireConfigured(await LoadAsync(worktreePath, cancellationToken));
        var resolvedProfileName = SelectProfileName(loaded.Config!, requestedProfile);

        // Parse and validate the edited service through the same model and rules the
        // preview-start path uses, so a config that would fail to start is rejected
        // here rather than silently written to disk.
        PreviewServiceConfig edited;
        try
        {
            edited = JsonSerializer.Deserialize<PreviewServiceConfig>(serviceConfigJson, JsonOptions)
                ?? throw new InvalidOperationException("Service config is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Service config is not valid JSON: {ex.Message}");
        }

        if (!string.Equals(edited.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Service config name '{edited.Name}' must match '{serviceName}'; this editor updates a service in place.");

        ValidateService(resolvedProfileName, edited);
        if (loaded.Config!.Preview!.Profiles.TryGetValue(resolvedProfileName, out var editedProfile) && editedProfile != null)
            PreviewVolumes.Validate(resolvedProfileName, editedProfile.Volumes, PreviewTemplates.Tokens(PreviewTemplates.Templates(edited)));

        var node = await LoadNodeAsync(loaded.ConfigPath, cancellationToken);
        if (node?["preview"]?["profiles"]?[resolvedProfileName]?["services"] is not JsonArray services)
            throw new InvalidOperationException($"Preview profile '{resolvedProfileName}' not found.");

        var index = -1;
        for (var i = 0; i < services.Count; i++)
        {
            if (string.Equals(services[i]?["name"]?.GetValue<string>(), serviceName, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            throw new InvalidOperationException($"Preview service '{serviceName}' not found in profile '{resolvedProfileName}'.");

        services[index] = JsonNode.Parse(serviceConfigJson);
        await files.WriteFileAsync(loaded.ConfigPath, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    // Parses ild.config.json into a mutable DOM so a single service entry can be read
    // or replaced without re-serializing the strongly-typed model (which would drop
    // fields the model does not surface). Tolerates comments and trailing commas, the
    // same as the strongly-typed loader.
    private async Task<JsonNode?> LoadNodeAsync(string configPath, CancellationToken cancellationToken)
    {
        await using var stream = await files.OpenFileForReadAsync(configPath, cancellationToken)
            ?? throw new FileNotFoundException(configPath);
        return await JsonNode.ParseAsync(
            stream,
            nodeOptions: null,
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true },
            cancellationToken);
    }

    private static JsonNode? FindServiceNode(JsonNode? root, string profileName, string serviceName)
    {
        if (root?["preview"]?["profiles"]?[profileName]?["services"] is not JsonArray services)
            return null;

        return services.FirstOrDefault(node =>
            string.Equals(node?["name"]?.GetValue<string>(), serviceName, StringComparison.OrdinalIgnoreCase));
    }

    public static void ValidateProfile(string profileName, PreviewProfileConfig profile)
    {
        if (profile.Services.Count == 0)
            throw new InvalidOperationException($"Preview profile '{profileName}' does not define any services.");

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in profile.Services)
        {
            ValidateService(profileName, service);
            if (!seenNames.Add(service.Name))
                throw new InvalidOperationException($"Preview profile '{profileName}' defines duplicate service name '{service.Name}'.");
        }

        ValidateVolumes(profileName, profile);
    }

    public static void ValidateVolumes(string profileName, PreviewProfileConfig profile)
        => PreviewVolumes.Validate(
            profileName,
            profile.Volumes,
            PreviewTemplates.Tokens(profile.Install.SelectMany(PreviewTemplates.Templates).Concat(profile.Services.SelectMany(PreviewTemplates.Templates))));

    private static readonly Regex SafeServiceNameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.Compiled);

    /// <summary>
    /// A service name becomes the file name of its log, so it must be a bare name: no separator, no
    /// <c>..</c>, nothing rooted. The one rule for the write side (validation) and the read side.
    /// </summary>
    internal static bool IsSafeServiceName(string? name) => name != null && SafeServiceNameRegex.IsMatch(name);

    /// <summary>
    /// True for an absolute http(s) URL whose host, as parsed, is the service's own loopback. The health probe is
    /// made from ILD's network position, so it may only ever name the run's own address, never a host the agent chose.
    /// </summary>
    internal static bool IsOwnLoopbackHealthUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https"
           && uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "0.0.0.0";

    internal static string HealthUrlNotLoopbackMessage(string serviceName)
        => $"Preview service '{serviceName}' healthUrl must name the service's own loopback (127.0.0.1, localhost, [::1] or 0.0.0.0).";

    /// <summary>Replaces template tokens with values of the right shape, to check a URL's host before the launch resolves it.</summary>
    private static string StandInTokens(string value)
        => PreviewTemplates.TokenRegex.Replace(value, match =>
        {
            var token = match.Groups[1].Value;
            if (string.Equals(token, "HOST", StringComparison.OrdinalIgnoreCase))
                return "0.0.0.0";
            if (string.Equals(token, "PORT", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("PORT:", StringComparison.OrdinalIgnoreCase))
                return "1";
            return "x";
        });

    /// <summary>
    /// Per-service validation shared by <see cref="ValidateProfile"/> and the config
    /// editor's <see cref="WriteServiceConfigAsync"/> — every rule a service must
    /// satisfy to be started. Duplicate-name detection across a profile stays in
    /// <see cref="ValidateProfile"/> since it is inherently cross-service.
    /// </summary>
    internal static void ValidateService(string profileName, PreviewServiceConfig service)
    {
        if (string.IsNullOrWhiteSpace(service.Name))
            throw new InvalidOperationException($"Preview profile '{profileName}' has a service with no name.");
        if (!IsSafeServiceName(service.Name))
            throw new InvalidOperationException($"Preview service name '{service.Name}' must start with a letter or digit and contain only letters, digits, '.', '_' and '-'.");
        if (string.IsNullOrWhiteSpace(service.Command))
            throw new InvalidOperationException($"Preview service '{service.Name}' has no command.");
        if (string.IsNullOrWhiteSpace(service.Port))
            throw new InvalidOperationException($"Preview service '{service.Name}' has no port alias.");
        if (string.IsNullOrWhiteSpace(service.HealthUrl))
            throw new InvalidOperationException($"Preview service '{service.Name}' must define healthUrl.");
        if (!IsOwnLoopbackHealthUrl(StandInTokens(service.HealthUrl)))
            throw new InvalidOperationException(HealthUrlNotLoopbackMessage(service.Name));
        if (service.SuggestedPort is <= 0)
            throw new InvalidOperationException($"Preview service '{service.Name}' has invalid suggestedPort '{service.SuggestedPort}'.");
        if (service.HealthTimeoutSeconds is { } healthTimeout and (< 1 or > MaxHealthTimeoutSeconds))
            throw new InvalidOperationException($"Preview service '{service.Name}' has invalid healthTimeoutSeconds '{healthTimeout}': it must be between 1 and {MaxHealthTimeoutSeconds}.");
    }
}
