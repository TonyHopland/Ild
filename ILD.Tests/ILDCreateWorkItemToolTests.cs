using ILD.Core.Services.Implementations;
using ILD.Core.Services.Interfaces;
using Moq;

namespace ILD.Tests;

public class ILDCreateWorkItemToolTests
{
    [Fact]
    public async Task ExecuteTool_create_workitem_succeeds_with_minimal_args()
    {
        // Arrange
        var createdId = Guid.NewGuid().ToString();
        var workItemManager = new Mock<IWorkItemManager>();
        workItemManager
            .Setup(m => m.CreateWorkItemAsync(It.IsAny<string>(), It.IsAny<string>(), (Guid?)null))
            .ReturnsAsync(createdId);

        using var db = new TestDb();
        var svc = new AIProviderService(db.Providers, workItemManager.Object, Mock.Of<IWorktreePreviewService>(), new HttpClient());

        // Act
        var args = System.Text.Json.JsonSerializer.Serialize(new { title = "Test feature", description = "Implement it" });
        var result = await svc.ExecuteToolAsync("ild.create_workitem", args, "/tmp/worktree");

        // Assert
        Assert.True(result.Success);
        Assert.Contains(createdId.ToString(), result.Output);
        workItemManager.Verify(m => m.CreateWorkItemAsync("Test feature", "Implement it", (Guid?)null), Times.Once);
    }

    [Fact]
    public async Task ExecuteTool_create_workitem_fails_on_missing_title()
    {
        // Arrange
        using var db = new TestDb();
        var svc = new AIProviderService(db.Providers, Mock.Of<IWorkItemManager>(), Mock.Of<IWorktreePreviewService>(), new HttpClient());

        // Act
        var args = System.Text.Json.JsonSerializer.Serialize(new { description = "no title" });
        var result = await svc.ExecuteToolAsync("ild.create_workitem", args, "/tmp/worktree");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("title", result.Error);
    }

    [Fact]
    public async Task ExecuteTool_create_workitem_fails_on_invalid_json()
    {
        // Arrange
        using var db = new TestDb();
        var svc = new AIProviderService(db.Providers, Mock.Of<IWorkItemManager>(), Mock.Of<IWorktreePreviewService>(), new HttpClient());

        // Act
        var result = await svc.ExecuteToolAsync("ild.create_workitem", "not-json", "/tmp/worktree");

        // Assert
        Assert.False(result.Success);
    }
}
