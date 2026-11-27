using ILD.Api.Controllers;
using ILD.Api.Services;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class NoAllowListTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AiProvidersController Controller()
    {
        var registry = new Mock<IAgentAdapterRegistry>();
        registry.Setup(r => r.GetAllSupportedProviderTypes()).Returns(["opencode", "claude-code"]);
        registry.Setup(r => r.GetModelSupport(It.IsAny<string>())).Returns((string type) => DeclaredModelSupport.For(type));
        registry.Setup(r => r.GetTraits(It.IsAny<string?>())).Returns((string? type) => DeclaredTraits.For(type));
        return new AiProvidersController(
            registry.Object, _db.Context, _db.Providers,
            new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance),
            Mock.Of<IManagedAgentProvisioner>());
    }

    [Fact]
    public async Task Creating_and_editing_a_provider_adds_no_network_entry()
    {
        var dto = new AiProviderDto { Name = "p", Type = "opencode", BaseUrl = "https://llm.example.com/v1", Model = "m" };
        var created = Assert.IsType<CreatedAtActionResult>(await Controller().Create(dto));
        var id = Guid.Parse(created.RouteValues!["id"]!.ToString()!);

        dto.BaseUrl = "https://other.example.com/v1";
        Assert.IsType<OkObjectResult>(await Controller().Update(id.ToString(), dto));

        Assert.Empty(await _db.Context.NetworkPolicyEntries.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_fresh_installation_starts_with_no_network_entries_in_monitor_mode()
    {
        await using var factory = new Integration.ApiFactory();
        using var scope = factory.Services.CreateScope();

        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().NetworkPolicyEntries.ToListAsync(Ct));
        Assert.Equal(NetworkMode.Monitor, (await scope.ServiceProvider.GetRequiredService<IEgressPolicy>().GetAsync(Ct)).Mode);
    }
}
