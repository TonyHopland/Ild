using System.Net;
using ILD.Data;
using ILD.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

public class AnalyticsClearApiTests
{
    private static void Seed(ApiFactory factory, DateTime runBucketDay, DateTime usageAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LoopRunAnalyticsBuckets.Add(new LoopRunAnalyticsBucket
        {
            Id = Guid.NewGuid(),
            BucketDate = runBucketDay,
            LoopTemplateId = Guid.NewGuid(),
            TemplateName = "gone-run",
            AiProvider = "claude",
            RunCount = 1,
        });
        db.ChatAnalyticsBuckets.Add(new ChatAnalyticsBucket
        {
            Id = Guid.NewGuid(),
            BucketDate = runBucketDay,
            LastRecordedAt = runBucketDay,
            AiProvider = "claude",
        });
        db.ChatUsages.Add(new ChatUsage
        {
            Id = Guid.NewGuid(),
            ChatSessionId = Guid.NewGuid(),
            AiProvider = "claude",
            CreatedAt = usageAt,
        });
        db.SaveChanges();
    }

    private static (int Runs, int Chats, int Usages) Counts(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (db.LoopRunAnalyticsBuckets.Count(), db.ChatAnalyticsBuckets.Count(), db.ChatUsages.Count());
    }

    [Fact]
    public async Task Clear_without_a_session_is_refused_and_deletes_nothing()
    {
        await using var factory = new ApiFactory();
        Seed(factory, DateTime.UtcNow.Date.AddDays(-400), DateTime.UtcNow.AddDays(-400));
        var ct = TestContext.Current.CancellationToken;

        var response = await factory.CreateClient().DeleteAsync("/api/v1/analytics", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal((1, 1, 1), Counts(factory));
    }

    [Fact]
    public async Task Clear_all_answers_204_and_empties_the_analytics_tables()
    {
        await using var factory = new ApiFactory();
        Seed(factory, DateTime.UtcNow.Date.AddDays(-1), DateTime.UtcNow);
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync("/api/v1/analytics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal((0, 0, 0), Counts(factory));
    }

    [Fact]
    public async Task Clear_older_than_days_answers_204_and_keeps_newer_rows()
    {
        await using var factory = new ApiFactory();
        Seed(factory, DateTime.UtcNow.Date.AddDays(-40), DateTime.UtcNow.AddDays(-40));
        Seed(factory, DateTime.UtcNow.Date.AddDays(-1), DateTime.UtcNow);
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync("/api/v1/analytics?olderThanDays=30", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal((1, 1, 1), Counts(factory));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("36501")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public async Task Clear_with_an_invalid_olderThanDays_answers_400_and_deletes_nothing(string value)
    {
        await using var factory = new ApiFactory();
        Seed(factory, DateTime.UtcNow.Date.AddDays(-400), DateTime.UtcNow.AddDays(-400));
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync($"/api/v1/analytics?olderThanDays={value}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal((1, 1, 1), Counts(factory));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("36500")]
    public async Task Clear_accepts_the_ends_of_the_olderThanDays_range(string value)
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync($"/api/v1/analytics?olderThanDays={value}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
