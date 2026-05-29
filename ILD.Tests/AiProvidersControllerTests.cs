using System.Text;
using ILD.Api.Controllers;
using ILD.Api.Services;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using ILD.Data.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ILD.Tests;

public class AiProvidersControllerTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly AppDbContext _db;
    private readonly Mock<IAgentAdapterRegistry> _registry = new();

    public AiProvidersControllerTests()
    {
        _conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _registry.Setup(r => r.GetAllSupportedProviderTypes()).Returns(["opencode", "pi", "claude-code"]);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    private AiProvidersController CreateController()
        => new(
            Mock.Of<IAIProviderService>(),
            _registry.Object,
            _db,
            new ProviderStore(_db),
            new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance));

    [Fact]
    public async Task GetAll_redacts_ApiKey_and_Config()
    {
        _db.AiProviders.Add(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "openai",
            Type = "OpenAI",
            BaseUrl = "https://api.openai.com",
            Model = "gpt-4",
            ApiKey = "sk-secret",
            Config = "{\"apiKey\":\"sk-secret\",\"model\":\"gpt-4\"}",
        });
        await _db.SaveChangesAsync();

        var controller = CreateController();
        var result = await controller.GetAll() as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        Assert.DoesNotContain("sk-secret", json);
        Assert.DoesNotContain("apiKey\":\"sk", json);
        Assert.DoesNotContain("\"config\":", json);
    }

    [Fact]
    public async Task GetById_redacts_ApiKey_and_Config()
    {
        var id = Guid.NewGuid();
        _db.AiProviders.Add(new AiProvider
        {
            Id = id,
            Name = "p",
            Type = "OpenAI",
            BaseUrl = "https://x",
            Model = "m",
            ApiKey = "sk-leaked",
            Config = "{\"apiKey\":\"sk-leaked\"}",
        });
        await _db.SaveChangesAsync();

        var controller = CreateController();
        var result = await controller.GetById(id.ToString()) as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        Assert.DoesNotContain("sk-leaked", json);
    }

    [Fact]
    public async Task GetAll_caps_take_at_500()
    {
        for (var i = 0; i < 600; i++)
        {
            _db.AiProviders.Add(new AiProvider
            {
                Id = Guid.NewGuid(),
                Name = $"p{i}",
                Type = "OpenAI",
                BaseUrl = "https://x",
                Model = "m",
            });
        }
        await _db.SaveChangesAsync();

        var controller = CreateController();
        var result = await controller.GetAll(skip: 0, take: 10000) as OkObjectResult;

        Assert.NotNull(result);
        var items = (System.Collections.IEnumerable)result!.Value!;
        Assert.Equal(500, items.Cast<object>().Count());
    }

    [Fact]
    public async Task GetAll_includes_supported_tools_from_backend_catalog()
    {
        _db.AiProviders.Add(new AiProvider
        {
            Id = Guid.NewGuid(),
            Name = "pi-default",
            Type = "pi",
            BaseUrl = "https://x",
            Model = "m",
        });
        await _db.SaveChangesAsync();

        var controller = CreateController();
        var result = await controller.GetAll() as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        Assert.Contains("supportedTools", json);
        Assert.Contains("\"Key\":\"read\"", json);
        Assert.Contains("\"Key\":\"write\"", json);
        Assert.Contains("\"Key\":\"execute\"", json);
        Assert.Contains("\"Key\":\"ild\"", json);
    }

    [Fact]
    public async Task Create_accepts_claude_code_with_empty_url_and_model()
    {
        var controller = CreateController();

        var result = await controller.Create(new AiProviderDto
        {
            Name = "claude-max",
            Type = "claude-code",
            BaseUrl = string.Empty,
            Model = string.Empty,
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(created.Value);
        Assert.Contains("claude-code", json);
    }

    [Fact]
    public async Task Create_rejects_non_cli_provider_without_url()
    {
        var controller = CreateController();

        var result = await controller.Create(new AiProviderDto
        {
            Name = "pi-default",
            Type = "pi",
            BaseUrl = string.Empty,
            Model = "gpt-4",
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("BaseUrl", json);
    }

    [Fact]
    public async Task Create_rejects_unsupported_provider_type()
    {
        var controller = CreateController();

        var result = await controller.Create(new AiProviderDto
        {
            Name = "legacy-openai",
            Type = "openai",
            BaseUrl = "https://api.example.com",
            Model = "gpt-4",
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("Unsupported AI provider type", json);
    }

    [Fact]
    public async Task SetDefault_promotes_provider_and_demotes_previous_default()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        _db.AiProviders.Add(new AiProvider
        {
            Id = firstId,
            Name = "first",
            Type = "pi",
            BaseUrl = "https://a",
            Model = "m",
            IsDefault = true,
        });
        _db.AiProviders.Add(new AiProvider
        {
            Id = secondId,
            Name = "second",
            Type = "pi",
            BaseUrl = "https://b",
            Model = "m",
            IsDefault = false,
        });
        await _db.SaveChangesAsync();

        var controller = CreateController();
        var result = await controller.SetDefault(secondId.ToString()) as OkObjectResult;

        Assert.NotNull(result);
        _db.ChangeTracker.Clear();
        var reloadedFirst = await _db.AiProviders.FindAsync(firstId);
        var reloadedSecond = await _db.AiProviders.FindAsync(secondId);
        Assert.False(reloadedFirst!.IsDefault);
        Assert.True(reloadedSecond!.IsDefault);
    }

    [Fact]
    public async Task SetDefault_unknown_id_returns_NotFound()
    {
        var controller = CreateController();
        var result = await controller.SetDefault(Guid.NewGuid().ToString());
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task SetDefault_invalid_id_returns_BadRequest()
    {
        var controller = CreateController();
        var result = await controller.SetDefault("not-a-guid");
        Assert.IsType<BadRequestResult>(result);
    }

    [Fact]
    public async Task Update_rejects_unsupported_provider_type()
    {
        var id = Guid.NewGuid();
        _db.AiProviders.Add(new AiProvider
        {
            Id = id,
            Name = "pi-default",
            Type = "pi",
            BaseUrl = "https://x",
            Model = "m",
        });
        await _db.SaveChangesAsync();

        var controller = CreateController();

        var result = await controller.Update(id.ToString(), new AiProviderDto
        {
            Id = id.ToString(),
            Name = "legacy-openai",
            Type = "openai",
            BaseUrl = "https://api.example.com",
            Model = "gpt-4",
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(badRequest.Value);
        Assert.Contains("Unsupported AI provider type", json);
    }
}
