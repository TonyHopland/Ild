using ILD.Core.Services.Remote;

namespace ILD.Tests;

public class WorkItemServerUnreachableTests
{
    [Fact]
    public void A_refused_call_or_the_clients_own_timeout_is_the_server_being_unreachable()
    {
        Assert.True(WorkItemServerUnreachable.Is(new HttpRequestException("Connection refused")));
        Assert.True(WorkItemServerUnreachable.Is(new TaskCanceledException("HttpClient.Timeout elapsed", new TimeoutException())));
    }

    [Fact]
    public void A_cancellation_the_caller_asked_for_is_not()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();

        Assert.False(WorkItemServerUnreachable.Is(new TaskCanceledException("aborted", null, aborted.Token)));
        Assert.False(WorkItemServerUnreachable.Is(new OperationCanceledException(aborted.Token)));
    }
}
