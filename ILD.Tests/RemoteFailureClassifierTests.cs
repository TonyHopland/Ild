using ILD.Core.Services.Remote;

namespace ILD.Tests;

public class RemoteFailureClassifierTests
{
    [Theory]
    [InlineData("remote: fatal error in commit_refs_rules\n ! [remote rejected] 5d1d3f3 -> b (fatal error in commit_refs_rules)")]
    [InlineData("remote: Internal Server Error")]
    [InlineData("HTTP 500")]
    [InlineData("HTTP 502")]
    [InlineData("fatal: unable to access 'https://x/': The requested URL returned error: 503")]
    [InlineData("error: RPC failed; curl 55 Recv failure")]
    [InlineData("fatal: early EOF")]
    [InlineData("Connection reset by peer")]
    [InlineData("Operation timed out after 30001 milliseconds")]
    [InlineData("fatal: Could not resolve host: github.com")]
    [InlineData("HTTP 429")]
    [InlineData("HTTP 403 (Retry-After: 30s): You have exceeded a secondary rate limit")]
    public void Temporary_failures_are_retried(string error)
        => Assert.True(RemoteFailureClassifier.IsTransient(error));

    [Theory]
    [InlineData("fatal: Authentication failed for 'https://github.com/o/r'")]
    [InlineData("HTTP 401")]
    [InlineData("HTTP 403")]
    [InlineData("HTTP 403: Resource not accessible by integration")]
    [InlineData("! [rejected] b -> b (non-fast-forward)")]
    [InlineData("! [rejected] b -> b (fetch first)")]
    [InlineData("remote: error: GH013: Repository rule violations found")]
    [InlineData("remote: refusing to allow an OAuth App to create or update workflow")]
    [InlineData("remote: Repository not found.")]
    [InlineData("HTTP 404")]
    [InlineData("HTTP 422")]
    [InlineData("something nobody has seen before")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_fails_fast(string? error)
        => Assert.False(RemoteFailureClassifier.IsTransient(error));

    [Fact]
    public void A_permanent_marker_wins_over_a_transient_one()
        => Assert.False(RemoteFailureClassifier.IsTransient("GH013 after an internal server error"));

    [Fact]
    public void Network_exceptions_are_transient_but_the_callers_cancellation_is_not()
    {
        Assert.True(RemoteFailureClassifier.IsTransient(new HttpRequestException("boom"), CancellationToken.None));
        Assert.True(RemoteFailureClassifier.IsTransient(new TaskCanceledException(), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(RemoteFailureClassifier.IsTransient(new TaskCanceledException(), cts.Token));
        Assert.False(RemoteFailureClassifier.IsTransient(new InvalidOperationException("x"), CancellationToken.None));
    }

    [Fact]
    public void Retry_After_stretches_the_wait_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), RemoteRetryPolicy.DelayFor(1, "HTTP 429 (Retry-After: 120s)"));
        Assert.Equal(RemoteRetryPolicy.MaxWait, RemoteRetryPolicy.DelayFor(1, "HTTP 429 (Retry-After: 99999s)"));
        var plain = RemoteRetryPolicy.DelayFor(1, "HTTP 502");
        Assert.InRange(plain.TotalSeconds, 12, 18);
    }

    [Fact]
    public void Jitter_at_its_upper_bound_never_costs_a_scheduled_retry()
    {
        var waited = TimeSpan.Zero;
        for (var retry = 1; retry <= RemoteRetryPolicy.MaxRetries; retry++)
        {
            var delay = RemoteRetryPolicy.DelayFor(retry, "HTTP 502", jitter: 1.2);
            Assert.True(RemoteRetryPolicy.AllowsRetry(waited, delay, "HTTP 502"), $"retry {retry}");
            waited += delay;
        }
    }

    [Fact]
    public void A_Retry_After_over_the_per_wait_cap_is_not_retried_early()
        => Assert.False(RemoteRetryPolicy.AllowsRetry(TimeSpan.Zero, RemoteRetryPolicy.MaxWait, "HTTP 429 (Retry-After: 600s)"));
}
