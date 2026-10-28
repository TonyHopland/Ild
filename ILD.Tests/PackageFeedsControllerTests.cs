using System.Text.Json;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

public sealed class PackageFeedsControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("abcd1234", "••••1234")]
    [InlineData("abc1234", "••••")]
    [InlineData("x", "••••")]
    public async Task The_pat_hint_shows_the_last_four_characters_only_of_a_pat_long_enough_to_spare_them(string pat, string hint)
    {
        var controller = new PackageFeedsController(_db.PackageFeeds, Mock.Of<IConnectionTester>());

        var result = await controller.Create(new PackageFeedsController.CreateFeedRequest
        {
            Name = "company",
            FeedUrl = "https://pkgs.dev.azure.com/example-org/_packaging/company",
            Pat = pat,
        }, TestContext.Current.CancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var json = JsonSerializer.SerializeToElement(created.Value);
        Assert.Equal(hint, json.GetProperty("patHint").GetString());
        Assert.False(json.TryGetProperty("pat", out _));
    }
}
