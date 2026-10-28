namespace ILD.Tests.Fakes;

internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => respond(request);
}
