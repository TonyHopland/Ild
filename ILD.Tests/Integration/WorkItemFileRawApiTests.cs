using System.Diagnostics;
using System.Net;
using System.Text;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// The Files tab's Download, over HTTP against a real worktree on disk: the
/// file as it is stored, served so the browser saves it and never renders it,
/// and nothing past the worktree's edge.
/// </summary>
public sealed class WorkItemFileRawApiTests : IDisposable
{
    private const string ItemWithWorktree = "WI-7";
    private const string ItemWithoutWorktree = "WI-8";

    private readonly string _tmp;
    private readonly string _worktree;
    private readonly string _outside;

    public WorkItemFileRawApiTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ild-raw-" + Guid.NewGuid().ToString("N"));
        _worktree = Path.Combine(_tmp, "wt");
        Directory.CreateDirectory(_worktree);
        var git = Process.Start(new ProcessStartInfo("git", ["init", "-q", _worktree]) { UseShellExecute = false })!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        _outside = Path.Combine(_tmp, "outside.txt");
        File.WriteAllText(_outside, "outside-secret\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory Factory()
    {
        var workItems = new Mock<IWorkItemManager>();
        // Running, not waiting on a human: reading is not the write's gate.
        workItems.Setup(m => m.GetWorkItemAsync(ItemWithWorktree)).ReturnsAsync(() => new WorkItemView
        {
            Id = ItemWithWorktree,
            Title = "T",
            Status = RemoteWorkItemStatus.Running,
            WorktreePath = _worktree,
        });
        workItems.Setup(m => m.GetWorkItemAsync(ItemWithoutWorktree)).ReturnsAsync(() => new WorkItemView
        {
            Id = ItemWithoutWorktree,
            Title = "T",
            Status = RemoteWorkItemStatus.Running,
            WorktreePath = null,
        });
        return new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<IWorkItemManager>();
            services.AddSingleton(workItems.Object);
        });
    }

    private static string Raw(string id, string path) =>
        $"/api/v1/workitems/{id}/files/raw?path={Uri.EscapeDataString(path)}";

    [Fact]
    public async Task A_file_is_served_byte_for_byte_as_a_download_the_browser_will_not_render()
    {
        // Markup that would run on the ILD origin if it were ever rendered, and
        // a binary past the size the content read inlines.
        var page = Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");
        var blob = new byte[(4 * 1024 * 1024) + 5];
        new Random(11).NextBytes(blob);
        blob[1] = 0;
        Directory.CreateDirectory(Path.Combine(_worktree, "site"));
        Directory.CreateDirectory(Path.Combine(_worktree, "bin", "data"));
        await File.WriteAllBytesAsync(Path.Combine(_worktree, "site", "index.html"), page, Ct);
        await File.WriteAllBytesAsync(Path.Combine(_worktree, "bin", "data", "blob.dat"), blob, Ct);

        await using var factory = Factory();
        var client = await factory.CreateAuthenticatedClientAsync();

        foreach (var (path, bytes, name) in new[]
                 {
                     ("site/index.html", page, "index.html"),
                     ("bin/data/blob.dat", blob, "blob.dat"),
                 })
        {
            var response = await client.GetAsync(Raw(ItemWithWorktree, path), Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(Ct));
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            var disposition = response.Content.Headers.ContentDisposition;
            Assert.Equal("attachment", disposition?.DispositionType);
            Assert.Equal(name, disposition!.FileNameStar ?? disposition.FileName?.Trim('"'));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    [Fact]
    public async Task Each_refusal_answers_in_the_terms_the_content_read_uses_and_serves_no_bytes()
    {
        Directory.CreateDirectory(Path.Combine(_worktree, "src"));
        File.CreateSymbolicLink(Path.Combine(_worktree, "escape.txt"), _outside);
        Directory.CreateSymbolicLink(Path.Combine(_worktree, "out"), _tmp);

        await using var factory = Factory();
        var client = await factory.CreateAuthenticatedClientAsync();

        async Task<HttpStatusCode> StatusOf(string url)
        {
            var response = await client.GetAsync(url, Ct);
            Assert.DoesNotContain("outside-secret", await response.Content.ReadAsStringAsync(Ct));
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf($"/api/v1/workitems/{ItemWithWorktree}/files/raw"));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(Raw(ItemWithWorktree, "  ")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(Raw("WI-unknown", "a.txt")));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(Raw(ItemWithoutWorktree, "a.txt")));

        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(Raw(ItemWithWorktree, "does-not-exist.txt")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(Raw(ItemWithWorktree, "src")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(Raw(ItemWithWorktree, "../outside.txt")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(Raw(ItemWithWorktree, _outside)));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(Raw(ItemWithWorktree, "escape.txt")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOf(Raw(ItemWithWorktree, "out/outside.txt")));
    }

    [Fact]
    public async Task Only_a_signed_in_user_can_download()
    {
        await File.WriteAllTextAsync(Path.Combine(_worktree, "a.txt"), "worktree-bytes\n", Ct);
        await using var factory = Factory();
        var user = await factory.CreateAuthenticatedClientAsync();
        // The route answers, so a refusal below is about the caller.
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(Raw(ItemWithWorktree, "a.txt"), Ct)).StatusCode);

        var anonymous = await factory.CreateClient().GetAsync(Raw(ItemWithWorktree, "a.txt"), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token);
        var asAgent = await agent.GetAsync(Raw(ItemWithWorktree, "a.txt"), Ct);
        Assert.Contains(asAgent.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.DoesNotContain("worktree-bytes", await asAgent.Content.ReadAsStringAsync(Ct));
    }
}
