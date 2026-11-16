using System.Diagnostics;
using ILD.Core.Services.RunHosts;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Core.Services.Implementations;

/// <summary>Waits for a launched service to answer its health URL.</summary>
internal interface IPreviewHealthProbe
{
    /// <param name="watched">
    /// A process that must stay up while it is probed: once it has exited and a probe has
    /// still failed, the wait ends at once rather than at the timeout.
    /// </param>
    /// <exception cref="InvalidOperationException">The service did not become healthy.</exception>
    Task WaitForHealthAsync(
        string serviceName, string healthUrl, string worktreePath, int timeoutSeconds, ManagedPreviewProcess? watched, CancellationToken cancellationToken);
}

internal sealed class HttpPreviewHealthProbe(IHttpClientFactory httpClientFactory, IRunWorkspace host, PreviewTimings timings) : IPreviewHealthProbe
{
    public async Task WaitForHealthAsync(
        string serviceName, string healthUrl, string worktreePath, int timeoutSeconds, ManagedPreviewProcess? watched, CancellationToken cancellationToken)
    {
        var timeout = timings.HealthTimeoutUnit * timeoutSeconds;
        var clock = Stopwatch.StartNew();
        using var client = httpClientFactory.CreateClient(PreviewHealthProbeClient.Name);
        var probed = await ReachableHealthUrlAsync(host, healthUrl, worktreePath, cancellationToken);
        while (clock.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.GetAsync(probed, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch
            {
                // Retry until timeout.
            }

            if (watched is { Process.HasExited: true })
            {
                throw new InvalidOperationException($"Preview service '{serviceName}' exited with code {watched.Process.ExitCode} before it became healthy.");
            }

            await Task.Delay(timings.HealthPollDelay, cancellationToken);
        }

        throw new InvalidOperationException(
            $"Preview service '{serviceName}' did not become healthy at {healthUrl} within {timeoutSeconds}s{(probed == healthUrl ? "" : $" (reached at {probed})")}.");
    }

    /// <summary>
    /// Where ILD reaches <paramref name="healthUrl"/>. A service's health URL names its own machine's loopback
    /// (<c>http://127.0.0.1:${PORT}/health</c>), which is ILD's only when the preview runs on ILD's machine. A preview in a
    /// run's pod or container is reached where the preview proxy reaches it, at the address the run host gives for the
    /// port. Any other host is refused: ILD never probes an address the agent chose.
    /// </summary>
    internal static async Task<string> ReachableHealthUrlAsync(IRunWorkspace host, string healthUrl, string worktreePath, CancellationToken cancellationToken)
    {
        if (!PreviewConfigLoader.IsOwnLoopbackHealthUrl(healthUrl))
            throw new InvalidOperationException("A preview's health URL must name the service's own loopback (127.0.0.1, localhost, [::1] or 0.0.0.0).");
        var uri = new Uri(healthUrl);

        var target = await host.PreviewTargetAsync(worktreePath, uri.Port, cancellationToken);
        return target.Host == uri.Host && target.Port == uri.Port
            ? healthUrl
            : new UriBuilder(uri) { Host = target.Host, Port = target.Port }.Uri.ToString();
    }
}

/// <summary>The named HTTP client the health probe uses.</summary>
public static class PreviewHealthProbeClient
{
    /// <summary>
    /// The client the health probe uses. It never follows a redirect, so a service cannot send ILD on to another host;
    /// a 3xx is not healthy and the probe tries the original URL again.
    /// </summary>
    public const string Name = "preview-health-probe";

    /// <summary>Makes the named client the health probe uses: no redirects followed, a short timeout per request.</summary>
    public static IHttpClientBuilder Configure(IHttpClientBuilder builder)
        => builder
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
}
