using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// The environment a launch's config files are built from, with <c>ILD_API_URL</c> as the launch in
/// <paramref name="workingDirectory"/> reaches ILD: the name its <c>NO_PROXY</c> carries, so the MCP server's calls go direct
/// rather than through the egress proxy. <c>ILD_API_TOKEN</c> is the launch's own <paramref name="agentApiToken"/>, never
/// whatever the process environment holds.
/// </summary>
public sealed class RunFacingEnvironment(
    IProcessEnvironment inner, IRunHost host, string? workingDirectory, string? agentApiToken = null) : IProcessEnvironment
{
    public string? Get(string name) => name switch
    {
        "ILD_API_URL" when inner.Get(name) is { } url => host.GatewayApiUrl(url, workingDirectory),
        "ILD_API_TOKEN" => agentApiToken,
        _ => inner.Get(name),
    };

    public void Set(string name, string? value) => inner.Set(name, value);
}
