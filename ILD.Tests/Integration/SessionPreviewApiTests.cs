using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using ILD.Data;
using ILD.Data.Entities;
using ILD.Data.Enums;
using ILD.Data.Stores.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// GET /api/v1/loopruns/{id}/sessions/preview serves a saved session a page of
/// raw lines at a time, located by raw byte offset.
/// </summary>
public sealed class SessionPreviewApiTests : IClassFixture<SessionPreviewApiTests.Host>
{
    private static readonly string[] ShortLines =
        ["{\"n\":0}", "{\"n\":1}", "plain text line", "{\"n\":3}", "{\"n\":4}", "{\"n\":5}", "{\"n\":6}"];

    private static readonly string[] LongLines = Enumerable.Range(0, 250).Select(n => $"{{\"line\":{n}}}").ToArray();

    private readonly Host _host;

    public SessionPreviewApiTests(Host host) => _host = host;

    [Fact]
    public async Task The_first_page_describes_the_session_and_reaches_its_end()
    {
        var page = await PreviewAsync("adapterName=Pi&sessionId=short");

        Assert.Equal("Pi", page.GetProperty("adapterName").GetString());
        Assert.Equal("short", page.GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.String, page.GetProperty("createdAt").ValueKind);
        Assert.True(page.TryGetProperty("updatedAt", out _));
        Assert.Equal("pi-jsonl", page.GetProperty("format").GetString());
        Assert.Equal(Offset(ShortLines, ShortLines.Length), page.GetProperty("byteLength").GetInt64());
        Assert.Equal(ShortLines.Length, page.GetProperty("eventCount").GetInt32());
        Assert.Equal(ShortLines, Events(page));
        Assert.Equal(0, page.GetProperty("fromOffset").GetInt64());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task Pages_follow_on_from_next_offset_until_the_end()
    {
        var first = await PreviewAsync("adapterName=Pi&sessionId=short&limit=3");
        Assert.Equal(ShortLines[..3], Events(first));
        Assert.Equal(0, first.GetProperty("fromOffset").GetInt64());
        Assert.Equal(Offset(ShortLines, 3), first.GetProperty("nextOffset").GetInt64());

        var second = await PreviewAsync($"adapterName=Pi&sessionId=short&fromOffset={Offset(ShortLines, 3)}&limit=3");
        Assert.Equal(ShortLines[3..6], Events(second));
        Assert.Equal(Offset(ShortLines, 3), second.GetProperty("fromOffset").GetInt64());
        Assert.Equal(Offset(ShortLines, 6), second.GetProperty("nextOffset").GetInt64());

        var last = await PreviewAsync($"adapterName=Pi&sessionId=short&fromOffset={Offset(ShortLines, 6)}&limit=3");
        Assert.Equal(ShortLines[6..], Events(last));
        Assert.Equal(Offset(ShortLines, 6), last.GetProperty("fromOffset").GetInt64());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task A_page_holds_200_lines_unless_asked_for_up_to_500()
    {
        var byDefault = await PreviewAsync("adapterName=Pi&sessionId=long");
        Assert.Equal(LongLines[..200], Events(byDefault));
        Assert.Equal(Offset(LongLines, 200), byDefault.GetProperty("nextOffset").GetInt64());

        var widest = await PreviewAsync("adapterName=Pi&sessionId=long&limit=500");
        Assert.Equal(LongLines, Events(widest));
        Assert.Equal(JsonValueKind.Null, widest.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task A_page_asked_for_at_the_end_is_empty_and_last()
    {
        var page = await PreviewAsync($"adapterName=Pi&sessionId=short&fromOffset={Offset(ShortLines, ShortLines.Length)}");

        Assert.Empty(Events(page));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(500, 0)]
    public async Task Tail_returns_the_last_lines_and_where_they_start(int tail, int firstLine)
    {
        var page = await PreviewAsync($"adapterName=Pi&sessionId=short&tail={tail}");

        Assert.Equal(ShortLines[firstLine..], Events(page));
        Assert.Equal(Offset(ShortLines, firstLine), page.GetProperty("fromOffset").GetInt64());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
    }

    [Theory]
    [InlineData("run", "adapterName=Pi&sessionId=short&tail=2&fromOffset=0")]
    [InlineData("run", "adapterName=Pi&sessionId=short&fromOffset=-1")]
    [InlineData("run", "adapterName=Pi&sessionId=short&fromOffset=PAST_END")]
    [InlineData("run", "adapterName=Pi&sessionId=short&limit=0")]
    [InlineData("run", "adapterName=Pi&sessionId=short&limit=501")]
    [InlineData("run", "adapterName=Pi&sessionId=short&tail=0")]
    [InlineData("run", "adapterName=Pi&sessionId=short&tail=501")]
    [InlineData("run", "sessionId=short")]
    [InlineData("run", "adapterName=Pi")]
    [InlineData("not-a-guid", "adapterName=Pi&sessionId=short")]
    public async Task A_malformed_request_is_refused(string run, string query)
    {
        var id = run == "run" ? _host.RunId.ToString() : run;
        query = query.Replace("PAST_END", (Offset(ShortLines, ShortLines.Length) + 1).ToString());

        var response = await _host.Client.GetAsync($"/api/v1/loopruns/{id}/sessions/preview?{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_session_is_not_found()
    {
        var response = await _host.Client.GetAsync(
            $"/api/v1/loopruns/{_host.RunId}/sessions/preview?adapterName=Pi&sessionId=missing", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_session_still_in_the_legacy_column_is_converted_and_then_served()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] events = ["{\"type\":\"user\",\"message\":\"hi\"}", "{\"type\":\"assistant\",\"n\":2}"];
        Guid legacyId;
        using (var scope = _host.Factory.Services.CreateScope())
        {
            legacyId = await SessionSnapshotTestSupport.SeedLegacyAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), _host.RunId, null, "ClaudeCode", "legacy-preview",
                SessionSnapshotTestSupport.LegacyClaudeWrapper("legacy-preview", events), ct);
        }

        var page = await PreviewAsync("adapterName=ClaudeCode&sessionId=legacy-preview");

        Assert.Equal(events, Events(page));
        Assert.Equal("claude-jsonl", page.GetProperty("format").GetString());
        Assert.Equal(Offset(events, events.Length), page.GetProperty("byteLength").GetInt64());
        Assert.Equal(events.Length, page.GetProperty("eventCount").GetInt32());
        using (var scope = _host.Factory.Services.CreateScope())
        {
            Assert.Null(await SessionSnapshotTestSupport.ReadLegacySessionJsonAsync(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(), legacyId, ct));
        }
    }

    private async Task<JsonElement> PreviewAsync(string query)
    {
        var response = await _host.Client.GetAsync(
            $"/api/v1/loopruns/{_host.RunId}/sessions/preview?{query}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static string[] Events(JsonElement page)
        => page.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// <summary>The raw byte offset at which line <paramref name="index"/> starts.</summary>
    private static long Offset(string[] lines, int index)
        => lines.Take(index).Sum(l => (long)Encoding.UTF8.GetByteCount(l) + 1);

    public sealed class Host : IAsyncLifetime
    {
        public ApiFactory Factory { get; } = new(configureServices: services =>
            services.RemoveHostedService<SessionSnapshotMigrationService>());

        public HttpClient Client { get; private set; } = null!;

        public Guid RunId { get; } = Guid.NewGuid();

        public async ValueTask InitializeAsync()
        {
            Client = await Factory.CreateAuthenticatedClientAsync();
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"preview-{RunId:N}" };
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
            db.LoopTemplates.Add(template);
            db.LoopTemplateVersions.Add(version);
            db.LoopRuns.Add(new LoopRun
            {
                Id = RunId,
                WorkItemId = "wi-preview",
                LoopTemplateVersionId = version.Id,
                Status = LoopRunStatus.Completed,
                StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();

            var store = scope.ServiceProvider.GetRequiredService<IAdapterSessionSnapshotStore>();
            await SessionSnapshotTestSupport.SeedAsync(
                store, SessionSnapshotKey.ForRun(RunId, "Pi", "short"), "pi-jsonl", string.Concat(ShortLines.Select(l => l + "\n")), default);
            await SessionSnapshotTestSupport.SeedAsync(
                store, SessionSnapshotKey.ForRun(RunId, "Pi", "long"), "pi-jsonl", string.Concat(LongLines.Select(l => l + "\n")), default);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }
}
