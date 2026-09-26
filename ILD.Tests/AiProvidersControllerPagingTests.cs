using ILD.Api.Controllers;
using ILD.Api.Services;
using ILD.Core.Services.Interfaces;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

/// <summary>
/// The browser pages through every provider to find tag holders, so each page
/// must continue exactly where the last one ended, even when names repeat.
/// </summary>
public class AiProvidersControllerPagingTests
{
    private static AiProvidersController Controller(TestDb db) => new(
        Mock.Of<IAIProviderService>(),
        Mock.Of<IAgentAdapterRegistry>(),
        db.Context,
        new ProviderStore(db.Context),
        new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance),
        Mock.Of<IManagedAgentProvisioner>());

    [Fact]
    public async Task Providers_sharing_a_name_page_in_a_stable_order_each_exactly_once()
    {
        using var db = new TestDb();
        foreach (var id in new[] { "ffffffff", "88888888", "11111111" })
        {
            db.Context.AiProviders.Add(new AiProvider
            {
                Id = Guid.Parse($"{id}-0000-0000-0000-000000000000"),
                Name = "Same",
                Type = "claude-code",
                Model = "m",
            });
            await db.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var controller = Controller(db);

        var paged = new List<Guid>();
        for (var skip = 0; skip < 3; skip++)
        {
            var page = Assert.IsType<OkObjectResult>(await controller.GetAll(skip, take: 1));
            paged.AddRange(((IEnumerable<object>)page.Value!)
                .Select(p => (Guid)p.GetType().GetProperty("id")!.GetValue(p)!));
        }

        var byId = await db.Context.AiProviders.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(byId, paged);
    }
}
