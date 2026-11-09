using ILD.Core.Services.RunHosts;

namespace ILD.Core.Services.Implementations.Adapters;

/// <summary>
/// The environment a launch's config files are built from, with <c>ILD_API_URL</c> as the run reaches ILD: the name its
/// <c>NO_PROXY</c> carries, so the MCP server's calls go direct rather than through the egress proxy.
/// </summary>
public sealed class RunFacingEnvironment(IProcessEnvironment inner, IRunHost host) : IProcessEnvironment
{
    public string? Get(string name)
        => name == "ILD_API_URL" && inner.Get(name) is { } url ? host.GatewayApiUrl(url) : inner.Get(name);

    public void Set(string name, string? value) => inner.Set(name, value);
}
