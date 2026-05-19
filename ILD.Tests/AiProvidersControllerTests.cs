using System.Text;
using ILD.Api.Controllers;
using ILD.Core.Services.Interfaces;
using ILD.Data;
using ILD.Data.DTOs;
using ILD.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ILD.Tests;

public class AiProvidersControllerTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly AppDbContext _db;

    public AiProvidersControllerTests()
    {
        _conn.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

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

        var controller = new AiProvidersController(Mock.Of<IAIProviderService>(), _db);
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

        var controller = new AiProvidersController(Mock.Of<IAIProviderService>(), _db);
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

        var controller = new AiProvidersController(Mock.Of<IAIProviderService>(), _db);
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

        var controller = new AiProvidersController(Mock.Of<IAIProviderService>(), _db);
        var result = await controller.GetAll() as OkObjectResult;

        Assert.NotNull(result);
        var json = System.Text.Json.JsonSerializer.Serialize(result!.Value);
        Assert.Contains("supportedTools", json);
        Assert.Contains("\"Key\":\"read\"", json);
        Assert.Contains("\"Key\":\"write\"", json);
        Assert.Contains("\"Key\":\"execute\"", json);
        Assert.Contains("\"Key\":\"ild\"", json);
    }
}
