using System.Net;
using System.Net.Http.Json;

namespace ILD.Tests.Integration;

[Collection("AuthEnvironment")]
public class WorkItemsIntegrationTests
{
    [Fact]
    public async Task GetAll_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/workitems");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAll_with_token_returns_200_and_empty_array()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/v1/workitems");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<object[]>();
        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [Fact]
    public async Task MergePr_without_token_returns_401()
    {
        await using var factory = new ApiFactory();
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/workitems/unknown/pr/merge", new { deleteBranch = true });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MergePr_for_unknown_work_item_returns_404()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("/api/v1/workitems/unknown/pr/merge", new { deleteBranch = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
