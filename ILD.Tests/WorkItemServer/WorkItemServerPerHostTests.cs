using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ILD.WorkItemServer.Dtos;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// WorkItem server test hosts that are up at the same time each keep the limits
/// and the API key they were given. Every host here is built before any of them
/// takes a request, so a value one of them left in the process environment would
/// reach the others.
/// </summary>
public sealed class WorkItemServerPerHostTests
{
    private const int Megabyte = 1024 * 1024;

    [Fact]
    public async Task Each_host_enforces_the_limits_it_was_constructed_with()
    {
        await using var tight = new AttachmentServerFactory(maxAttachmentMb: 1, maxTotalMb: 1);
        await using var roomy = new AttachmentServerFactory();
        using var tightClient = tight.AuthedClient();
        using var roomyClient = roomy.AuthedClient();

        var tightItem = await CreateWorkItemAsync(tightClient);
        var roomyItem = await CreateWorkItemAsync(roomyClient);

        Assert.Equal(HttpStatusCode.BadRequest, await UploadAsync(tightClient, tightItem, 2 * Megabyte));
        Assert.Equal(HttpStatusCode.Created, await UploadAsync(roomyClient, roomyItem, 2 * Megabyte));
    }

    [Fact]
    public async Task Each_host_accepts_only_its_own_api_key()
    {
        await using var attachments = new AttachmentServerFactory();
        await using var api = new WorkItemServerApiTests.Factory();
        using var attachmentsClient = attachments.CreateClient();
        using var apiClient = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await ListAsync(attachmentsClient, AttachmentServerFactory.ApiKey));
        Assert.Equal(HttpStatusCode.OK, await ListAsync(apiClient, WorkItemServerApiTests.Factory.ApiKey));
        Assert.Equal(HttpStatusCode.Unauthorized, await ListAsync(attachmentsClient, WorkItemServerApiTests.Factory.ApiKey));
        Assert.Equal(HttpStatusCode.Unauthorized, await ListAsync(apiClient, AttachmentServerFactory.ApiKey));
    }

    private static async Task<HttpStatusCode> ListAsync(HttpClient client, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/workitems");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<string> CreateWorkItemAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = "per-host" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<WorkItemDto>())!.Id;
    }

    private static async Task<HttpStatusCode> UploadAsync(HttpClient client, string workItemId, int size)
    {
        using var body = AttachmentUpload.Of("same.bin", "application/octet-stream", AttachmentUpload.Bytes(size));
        using var response = await client.PostAsync($"/workitems/{workItemId}/attachments", body);
        return response.StatusCode;
    }
}
