using ILD.WorkItemServer.Attachments;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// A <see cref="WorkItemServerFactory"/> with its attachment limits supplied per
/// instance, replacing what the host read from the process environment.
/// </summary>
internal sealed class AttachmentServerFactory : WorkItemServerFactory
{
    public const string ApiKey = "attachments-test-key";

    private readonly AttachmentLimits _limits;

    /// <param name="maxAttachmentMb">Per file; null means the default.</param>
    /// <param name="maxTotalMb">Per work item; null means the default.</param>
    public AttachmentServerFactory(int? maxAttachmentMb = null, int? maxTotalMb = null)
        : base(ApiKey)
    {
        _limits = AttachmentLimits.FromEnvironment(name => name switch
        {
            AttachmentLimits.MaxAttachmentMbVariable => maxAttachmentMb?.ToString(),
            AttachmentLimits.MaxAttachmentsTotalMbVariable => maxTotalMb?.ToString(),
            _ => null,
        });
    }

    protected override void ConfigureServices(IServiceCollection services) => services.ReplaceSingleton(_limits);
}

/// <summary>
/// One default <see cref="AttachmentServerFactory"/> and its authed client for every test of a class
/// that reads and writes only the work items it creates itself.
/// </summary>
public sealed class AttachmentServer : IAsyncLifetime
{
    private AttachmentServerFactory _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _factory.Services;

    public ValueTask InitializeAsync()
    {
        _factory = new AttachmentServerFactory();
        Client = _factory.AuthedClient();
        return ValueTask.CompletedTask;
    }

    /// <summary>A context on the same database, for reading and writing rows the API does not expose.</summary>
    public ILD.WorkItemServer.WorkItemServerDbContext NewDbContext() => _factory.NewDbContext();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }
}
