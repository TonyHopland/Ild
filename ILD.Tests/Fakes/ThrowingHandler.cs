namespace ILD.Tests.Fakes;

/// <summary>Fails every request the way a host that cannot be reached does.</summary>
internal sealed class ThrowingHandler(string message = "connection reset") : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new HttpRequestException(message);
}
