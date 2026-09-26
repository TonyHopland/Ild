using System.Net;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

public class WorkItemsIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/workitems", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/workitems", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task MergePr_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/workitems/unknown/pr/merge", new { deleteBranch = true }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MergePr_for_unknown_work_item_returns_404()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/workitems/unknown/pr/merge", new { deleteBranch = true }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
