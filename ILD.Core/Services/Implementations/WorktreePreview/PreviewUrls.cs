using System.Text.RegularExpressions;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;

namespace ILD.Core.Services.Implementations;

/// <summary>The preview hostnames and URLs: how a hostname names a work item's service, and the address a service is advertised at.</summary>
internal static class PreviewUrls
{
    /// <summary>
    /// A preview hostname's leading label: <c>wi-{workItemId}</c>, optionally
    /// followed by <c>-{serviceName}</c>. Anchoring the id to digits is what keeps
    /// the split unambiguous for service names that themselves contain hyphens
    /// (<c>wi-12-work-item-server</c> is item 12's <c>work-item-server</c>, not
    /// item <c>12-work</c>'s <c>item-server</c>).
    /// </summary>
    private static readonly Regex PreviewHostLabelRegex =
        new(@"^wi-(?<id>[0-9]+)(?:-(?<service>.+))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Splits a preview hostname's leading label into its work item id and optional service name.</summary>
    internal static bool TryParsePreviewHostLabel(string? label, out string workItemId, out string? serviceName)
    {
        var match = PreviewHostLabelRegex.Match(label?.Trim() ?? string.Empty);
        workItemId = match.Success ? match.Groups["id"].Value : string.Empty;
        serviceName = match.Success && match.Groups["service"].Success ? match.Groups["service"].Value : null;
        return match.Success;
    }

    /// <summary>One service of a running preview, as the proxy's target choice sees it.</summary>
    internal sealed record PreviewServiceRow(string Name, bool Public, bool RewriteHost, bool IsRunning, string PortAlias, int Port);

    /// <summary>
    /// Picks the service a preview hostname addresses: the named one, or with no name
    /// the single running service marked public.
    /// </summary>
    internal static PreviewTarget SelectPreviewTarget(string workItemId, string? serviceName, IReadOnlyList<PreviewServiceRow> rows)
    {
        PreviewServiceRow? row;
        if (serviceName == null)
        {
            var publicRows = rows.Where(r => r.Public && r.IsRunning).ToList();
            if (publicRows.Count == 0)
            {
                return PreviewTarget.Failed(
                    PreviewTargetOutcome.ServiceNotRunning,
                    $"No service marked \"public\": true is running in work item {workItemId}'s preview. "
                    + $"Start it, or address one service directly as wi-{workItemId}-<serviceName>.");
            }

            // Picking the first would silently serve one of them under a hostname
            // that just as fairly describes the others.
            if (publicRows.Count > 1)
            {
                var names = string.Join(", ", publicRows.Select(r => $"wi-{workItemId}-{r.Name}"));
                return PreviewTarget.Failed(
                    PreviewTargetOutcome.AmbiguousService,
                    $"Work item {workItemId}'s preview is running {publicRows.Count} services marked "
                    + $"\"public\": true, so this hostname does not name one. Use {names}.");
            }

            row = publicRows[0];
        }
        else
        {
            row = rows.FirstOrDefault(r => string.Equals(r.Name, serviceName, StringComparison.OrdinalIgnoreCase));
            if (row == null || !row.IsRunning)
            {
                return PreviewTarget.Failed(
                    PreviewTargetOutcome.ServiceNotRunning,
                    $"Preview service '{serviceName}' is not running in work item {workItemId}'s preview.");
            }
        }

        if (row.Port <= 0)
        {
            return PreviewTarget.Failed(
                PreviewTargetOutcome.ServiceNotRunning,
                $"Preview service '{row.Name}' has no port allocated for alias '{row.PortAlias}'.");
        }

        return PreviewTarget.Resolved(row.Port, row.Name, row.RewriteHost);
    }

    /// <summary>The loopback address of a service's allocated port; null without one.</summary>
    internal static string? ComposeLocalUrl(int? port)
        => port is int allocated ? $"http://127.0.0.1:{allocated}" : null;

    /// <summary>The precedence of <see cref="PreviewResponses.BuildPublicUrl"/> over values already resolved.</summary>
    internal static string? ComposePublicUrl(
        string? authoredUrl, PreviewProxyBase proxyBase, string? workItemId, string serviceName,
        string publicHost, int? port, bool singlePublicService)
    {
        if (authoredUrl != null)
            return authoredUrl;

        var proxyLabel = BuildPreviewHostLabel(workItemId, singlePublicService ? null : serviceName);
        if (proxyBase.Enabled && proxyLabel != null)
            return proxyBase.BuildUrl(proxyLabel);

        return port is int allocated ? $"http://{publicHost}:{allocated}" : null;
    }

    /// <summary>
    /// The hostname label for a work item's preview, or null when one cannot be
    /// formed — no work item id, or a service name that is not a legal DNS label
    /// (which <c>ild.config.json</c> does not otherwise constrain).
    /// </summary>
    internal static string? BuildPreviewHostLabel(string? workItemId, string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            return null;

        if (serviceName == null)
            return $"wi-{workItemId}";

        return IsDnsLabelSegment(serviceName) ? $"wi-{workItemId}-{serviceName}" : null;
    }

    private static bool IsDnsLabelSegment(string value)
        => value.Length > 0
            && value[0] != '-'
            && value[^1] != '-'
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
