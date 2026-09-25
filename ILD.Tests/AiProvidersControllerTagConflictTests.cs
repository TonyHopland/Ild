using ILD.Api.Controllers;
using ILD.Api.Services;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using ILD.Data.Stores.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// A provider save that the database refuses is a 409 only when a concurrent
/// save took one of its tags; any other failure keeps propagating as before.
/// </summary>
public class AiProvidersControllerTagConflictTests
{
    private static AiProvidersController Controller(TestDb db, IProviderStore store)
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.GetAllSupportedProviderTypes()).Returns(["claude-code"]);
        registry.Setup(r => r.GetModelSupport(It.IsAny<string>()))
            .Returns((string type) => DeclaredModelSupport.For(type));
        return new AiProvidersController(
            Mock.Of<IAIProviderService>(),
            registry.Object,
            db.Context,
            store,
            new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance),
            Mock.Of<IManagedAgentProvisioner>());
    }

    /// <summary>A store whose create fails after <paramref name="meanwhile"/> ran, reading tags from the real database.</summary>
    private static IProviderStore FailingCreate(TestDb db, Func<Task> meanwhile)
    {
        var real = new ProviderStore(db.Context);
        var store = new Mock<IProviderStore>();
        store.Setup(s => s.GetAiProviderByTagAsync(It.IsAny<string>()))
            .Returns((string tag) => real.GetAiProviderByTagAsync(tag));
        store.Setup(s => s.CreateAiProviderAsync(It.IsAny<AiProvider>(), It.IsAny<IReadOnlyList<string>?>()))
            .Returns(async () =>
            {
                await meanwhile();
                throw new DbUpdateException("refused");
            });
        return store.Object;
    }

    private static AiProviderDto Dto(params string[] tags) => new()
    {
        Name = "mine",
        Type = "claude-code",
        BaseUrl = string.Empty,
        Model = string.Empty,
        Tags = [.. tags],
    };

    [Fact]
    public async Task A_save_refused_because_another_save_took_its_tag_is_a_conflict_naming_both()
    {
        using var db = new TestDb();
        var store = FailingCreate(db, () => new ProviderStore(db.Context).CreateAiProviderAsync(
            new AiProvider { Id = Guid.NewGuid(), Name = "Rival", Type = "claude-code", Model = "m" },
            ["fast"]));

        var result = await Controller(db, store).Create(Dto("QA", "Fast"));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var error = System.Text.Json.JsonSerializer.SerializeToElement(conflict.Value).GetProperty("error").GetString();
        Assert.Contains("'Fast'", error);
        Assert.Contains("'Rival'", error);
    }

    [Fact]
    public async Task A_save_refused_for_another_reason_is_not_reported_as_a_tag_conflict()
    {
        using var db = new TestDb();
        var store = FailingCreate(db, () => Task.CompletedTask);

        await Assert.ThrowsAsync<DbUpdateException>(() => Controller(db, store).Create(Dto("QA", "Fast")));
    }
}
