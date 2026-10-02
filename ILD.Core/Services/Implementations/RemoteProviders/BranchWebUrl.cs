namespace ILD.Core.Services.Implementations.RemoteProviders;

/// <summary>
/// The web page of a branch on its forge, derived from the repository's clone
/// URL. The clone URL often carries credentials, so the link is assembled from
/// the URL's scheme, server and path only. Anything that cannot be pinned
/// exactly (a non-http clone URL, an unknown forge type) yields no link.
/// </summary>
public static class BranchWebUrl
{
    public static string? For(string providerType, string cloneUrl, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            return null;
        if (!Uri.TryCreate(cloneUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        path = path.TrimEnd('/');
        if (path.Length == 0)
            return null;

        var web = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + path;
        var segments = string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));

        if (string.Equals(providerType, "GitHub", StringComparison.OrdinalIgnoreCase))
            return $"{web}/tree/{segments}";
        if (string.Equals(providerType, "Forgejo", StringComparison.OrdinalIgnoreCase))
            return $"{web}/src/branch/{segments}";
        if (string.Equals(providerType, "AzureDevOps", StringComparison.OrdinalIgnoreCase))
            return $"{web}?version=GB{Uri.EscapeDataString(branch)}";
        return null;
    }
}
