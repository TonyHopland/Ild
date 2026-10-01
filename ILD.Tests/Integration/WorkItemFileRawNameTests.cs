using System.Diagnostics;
using System.Net;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace ILD.Tests.Integration;

/// <summary>
/// The name a raw download is saved under belongs to the file that was served,
/// however the path to it was spelled: resolution settles a trailing separator
/// or a "." onto the file, and the name has to follow it there, or the response
/// loses its attachment disposition with it.
/// </summary>
public sealed class WorkItemFileRawNameTests : IDisposable
{
    private const string Item = "WI-7";

    private readonly string _tmp;
    private readonly string _worktree;

    public WorkItemFileRawNameTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ild-raw-name-" + Guid.NewGuid().ToString("N"));
        _worktree = Path.Combine(_tmp, "wt");
        Directory.CreateDirectory(Path.Combine(_worktree, "site"));
        var git = Process.Start(new ProcessStartInfo("git", ["init", "-q", _worktree]) { UseShellExecute = false })!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
        File.WriteAllText(Path.Combine(_worktree, "site", "page.html"), "<p>hi</p>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("site/page.html/")]
    [InlineData("site/page.html//")]
    [InlineData("site/page.html/.")]
    [InlineData("site/./page.html")]
    public async Task A_file_reached_by_any_spelling_is_saved_under_its_own_name(string path)
    {
        var workItems = new Mock<IWorkItemManager>();
        workItems.Setup(m => m.GetWorkItemAsync(Item)).ReturnsAsync(() => new WorkItemView
        {
            Id = Item,
            Title = "T",
            Status = RemoteWorkItemStatus.Running,
            WorktreePath = _worktree,
        });
        await using var factory = new ApiFactory(configureServices: services =>
        {
            services.RemoveAll<IWorkItemManager>();
            services.AddSingleton(workItems.Object);
        });
        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync(
            $"/api/v1/workitems/{Item}/files/raw?path={Uri.EscapeDataString(path)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal("page.html", disposition.FileNameStar ?? disposition.FileName);
    }
}
