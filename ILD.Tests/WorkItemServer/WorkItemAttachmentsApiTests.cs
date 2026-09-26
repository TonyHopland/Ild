using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.WorkItemServer;
using ILD.WorkItemServer.Domain;
using ILD.WorkItemServer.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ILD.Tests.WorkItemServer;

/// <summary>
/// The WorkItem server's attachment surface: bytes live in its own database and
/// nowhere else, and every one of the four operations is reachable over HTTP
/// with no ILD instance and no UI in front of it.
/// </summary>
public sealed class WorkItemAttachmentsApiTests : IAsyncLifetime
{
    private AttachmentServerFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new AttachmentServerFactory();
        _client = _factory.AuthedClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<string> CreateWorkItemAsync(string title = "with attachments")
    {
        var created = await _client.PostAsJsonAsync("/workitems", new CreateWorkItemRequest { Title = title });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<WorkItemDto>())!.Id;
    }

    private async Task<JsonElement> UploadAsync(string workItemId, MultipartFormDataContent body, HttpStatusCode expected = HttpStatusCode.Created)
    {
        using (body)
        {
            var resp = await _client.PostAsync($"/workitems/{workItemId}/attachments", body);
            Assert.Equal(expected, resp.StatusCode);
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        }
    }

    private async Task<JsonElement> ListAsync(string workItemId)
    {
        var resp = await _client.GetAsync($"/workitems/{workItemId}/attachments");
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Upload_answers_with_the_created_metadata_and_keeps_the_bytes_in_the_database()
    {
        var id = await CreateWorkItemAsync();
        var bytes = AttachmentUpload.Bytes(4096);

        var created = await UploadAsync(id, AttachmentUpload.Of("sketch.png", "image/png", bytes));

        Assert.Equal(JsonValueKind.Array, created.ValueKind);
        var metadata = Assert.Single(created.EnumerateArray().ToList());
        Assert.Equal("sketch.png", metadata.GetProperty("fileName").GetString());
        Assert.Equal("image/png", metadata.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, metadata.GetProperty("sizeBytes").GetInt64());
        var attachmentId = metadata.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, attachmentId);

        await using var db = _factory.NewDbContext();
        var row = await db.Set<WorkItemAttachment>().SingleAsync(a => a.Id == attachmentId, TestContext.Current.CancellationToken);
        var owner = await db.WorkItems.SingleAsync(w => w.Id == id, TestContext.Current.CancellationToken);
        Assert.Equal(owner.InternalId, row.WorkItemId);
        Assert.Equal(bytes, row.Content);
        Assert.Equal(bytes.Length, row.SizeBytes);
    }

    [Fact]
    public async Task The_listing_carries_metadata_and_never_the_bytes()
    {
        var id = await CreateWorkItemAsync();
        var bytes = Encoding.UTF8.GetBytes("the quick brown fox");
        await UploadAsync(id, AttachmentUpload.Of("notes.txt", "text/plain", bytes));

        var resp = await _client.GetAsync($"/workitems/{id}/attachments", TestContext.Current.CancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var listed = Assert.Single(JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList());

        Assert.Equal("notes.txt", listed.GetProperty("fileName").GetString());
        Assert.Equal("text/plain", listed.GetProperty("contentType").GetString());
        Assert.Equal(bytes.Length, listed.GetProperty("sizeBytes").GetInt64());
        Assert.True(listed.TryGetProperty("createdAt", out _));
        foreach (var byteCarrier in new[] { "content", "bytes", "data" })
            Assert.False(listed.TryGetProperty(byteCarrier, out _), $"the listing exposes '{byteCarrier}'");
        Assert.DoesNotContain(Convert.ToBase64String(bytes), raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_download_returns_the_stored_bytes_and_type_as_an_attachment_that_cannot_be_sniffed()
    {
        var id = await CreateWorkItemAsync();
        var bytes = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");
        var created = await UploadAsync(id, AttachmentUpload.Of("page.html", "text/html", bytes));
        var attachmentId = created[0].GetProperty("id").GetGuid();

        var resp = await _client.GetAsync($"/workitems/{id}/attachments/{attachmentId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(bytes, await resp.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/html", resp.Content.Headers.ContentType?.MediaType);
        // Without both of these an uploaded page executes on the server's origin.
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("page.html", resp.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", Assert.Single(resp.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Delete_answers_204_and_removes_the_row()
    {
        var id = await CreateWorkItemAsync();
        var created = await UploadAsync(id, AttachmentUpload.Of("gone.bin", "application/octet-stream", AttachmentUpload.Bytes(32)));
        var attachmentId = created[0].GetProperty("id").GetGuid();

        var deleted = await _client.DeleteAsync($"/workitems/{id}/attachments/{attachmentId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await ListAsync(id)).EnumerateArray().ToList());
        await using var db = _factory.NewDbContext();
        Assert.False(await db.Set<WorkItemAttachment>().AnyAsync(a => a.Id == attachmentId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_attachment_route_answers_404_for_an_unknown_work_item()
    {
        var unknownItem = "999999";
        var unknownAttachment = Guid.NewGuid();

        // A real item first, so "404" cannot simply mean "no such route".
        var known = await CreateWorkItemAsync();
        var real = await UploadAsync(known, AttachmentUpload.Of("real.bin", "application/octet-stream", AttachmentUpload.Bytes(8)));
        var served = await _client.GetAsync($"/workitems/{known}/attachments/{real[0].GetProperty("id").GetGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        var list = await _client.GetAsync($"/workitems/{unknownItem}/attachments", TestContext.Current.CancellationToken);
        var download = await _client.GetAsync($"/workitems/{unknownItem}/attachments/{unknownAttachment}", TestContext.Current.CancellationToken);
        var delete = await _client.DeleteAsync($"/workitems/{unknownItem}/attachments/{unknownAttachment}", TestContext.Current.CancellationToken);
        using var body = AttachmentUpload.Of("x.txt", "text/plain", AttachmentUpload.Bytes(8));
        var upload = await _client.PostAsync($"/workitems/{unknownItem}/attachments", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
    }

    [Fact]
    public async Task An_unknown_attachment_id_on_a_real_work_item_is_404()
    {
        var id = await CreateWorkItemAsync();
        var other = await CreateWorkItemAsync("somebody else's");
        var created = await UploadAsync(other, AttachmentUpload.Of("theirs.bin", "application/octet-stream", AttachmentUpload.Bytes(16)));
        var someoneElses = created[0].GetProperty("id").GetGuid();

        var missing = await _client.GetAsync($"/workitems/{id}/attachments/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        var wrongItem = await _client.GetAsync($"/workitems/{id}/attachments/{someoneElses}", TestContext.Current.CancellationToken);
        var deleteWrongItem = await _client.DeleteAsync($"/workitems/{id}/attachments/{someoneElses}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongItem.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteWrongItem.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_work_item_deletes_its_attachments_and_leaves_no_orphans()
    {
        var doomed = await CreateWorkItemAsync("doomed");
        var survivor = await CreateWorkItemAsync("survivor");
        await UploadAsync(doomed, AttachmentUpload.Of("a.bin", "application/octet-stream", AttachmentUpload.Bytes(64)));
        await UploadAsync(doomed, AttachmentUpload.Of("b.bin", "application/octet-stream", AttachmentUpload.Bytes(64, seed: 9)));
        await UploadAsync(survivor, AttachmentUpload.Of("keep.bin", "application/octet-stream", AttachmentUpload.Bytes(64)));

        var deleted = await _client.DeleteAsync($"/workitems/{doomed}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await using var db = _factory.NewDbContext();
        var doomedKey = await db.WorkItems.Where(w => w.Id == doomed).Select(w => (int?)w.InternalId).FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        Assert.Null(doomedKey);

        var remaining = await db.Set<WorkItemAttachment>().ToListAsync(TestContext.Current.CancellationToken);
        var liveKeys = await db.WorkItems.Select(w => w.InternalId).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("keep.bin", Assert.Single(remaining).FileName);
        Assert.All(remaining, a => Assert.Contains(a.WorkItemId, liveKeys));
    }

    [Fact]
    public async Task A_missing_content_type_is_stored_and_served_as_application_octet_stream()
    {
        var id = await CreateWorkItemAsync();
        var created = await UploadAsync(id, AttachmentUpload.Of("mystery", contentType: null, AttachmentUpload.Bytes(24)));

        Assert.Equal("application/octet-stream", created[0].GetProperty("contentType").GetString());

        var download = await _client.GetAsync($"/workitems/{id}/attachments/{created[0].GetProperty("id").GetGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_stored_content_type_that_cannot_be_parsed_is_served_as_application_octet_stream()
    {
        var id = await CreateWorkItemAsync();
        var bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        var attachmentId = Guid.NewGuid();

        await using (var seed = _factory.NewDbContext())
        {
            var owner = await seed.WorkItems.SingleAsync(w => w.Id == id, TestContext.Current.CancellationToken);
            seed.Set<WorkItemAttachment>().Add(new WorkItemAttachment
            {
                Id = attachmentId,
                WorkItemId = owner.InternalId,
                FileName = "hand-written.svg",
                ContentType = "not a content type",
                SizeBytes = bytes.Length,
                Content = bytes,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var download = await _client.GetAsync($"/workitems/{id}/attachments/{attachmentId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task A_valid_content_type_round_trips_unchanged()
    {
        var id = await CreateWorkItemAsync();
        var created = await UploadAsync(id, AttachmentUpload.Of("report.json", "application/json", Encoding.UTF8.GetBytes("{}")));

        Assert.Equal("application/json", created[0].GetProperty("contentType").GetString());
        Assert.Equal("application/json", (await ListAsync(id))[0].GetProperty("contentType").GetString());

        var download = await _client.GetAsync($"/workitems/{id}/attachments/{created[0].GetProperty("id").GetGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal("application/json", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task More_than_ten_files_in_one_request_is_refused_and_nothing_is_stored()
    {
        var id = await CreateWorkItemAsync();
        var files = Enumerable.Range(0, 11)
            .Select(i => ($"f{i}.bin", (string?)"application/octet-stream", AttachmentUpload.Bytes(16, (byte)i)))
            .ToArray();

        using var body = AttachmentUpload.Of(files);
        var resp = await _client.PostAsync($"/workitems/{id}/attachments", body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var error = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("10", error!, StringComparison.Ordinal);
        Assert.Empty((await ListAsync(id)).EnumerateArray().ToList());
    }

    [Fact]
    public async Task Ten_files_in_one_request_are_accepted()
    {
        var id = await CreateWorkItemAsync();
        var files = Enumerable.Range(0, 10)
            .Select(i => ($"f{i}.bin", (string?)"application/octet-stream", AttachmentUpload.Bytes(16, (byte)i)))
            .ToArray();

        var created = await UploadAsync(id, AttachmentUpload.Of(files));

        Assert.Equal(10, created.EnumerateArray().Count());
        Assert.Equal(10, (await ListAsync(id)).EnumerateArray().Count());
    }

    [Fact]
    public void Attachments_are_a_cascading_child_of_the_work_items_key()
    {
        using var db = _factory.NewDbContext();
        var entity = db.Model.FindEntityType(typeof(WorkItemAttachment));

        Assert.NotNull(entity);
        Assert.Equal(nameof(WorkItemAttachment.Id), Assert.Single(entity!.FindPrimaryKey()!.Properties).Name);
        Assert.Equal(typeof(Guid), entity.FindPrimaryKey()!.Properties[0].ClrType);

        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(WorkItem), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(WorkItem.InternalId), Assert.Single(foreignKey.PrincipalKey.Properties).Name);
        Assert.Equal(nameof(WorkItemAttachment.WorkItemId), Assert.Single(foreignKey.Properties).Name);
        // The server deletes a work item without ever loading its attachments,
        // so nothing but the database's own cascade collects them.
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void One_scaffolded_migration_adds_the_attachments_table()
    {
        var migrations = Directory.GetFiles(Path.Combine(RepositoryFiles.Root, "ILD.WorkItemServer", "Migrations"), "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(f => !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("WorkItemAttachments", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Single(migrations);
        // The snapshot is only updated by scaffolding; a hand-written migration leaves it behind.
        Assert.Contains("WorkItemAttachments", RepositoryFiles.ReadAllText(
            Path.Combine("ILD.WorkItemServer", "Migrations", "WorkItemServerDbContextModelSnapshot.cs")), StringComparison.Ordinal);
    }
}
