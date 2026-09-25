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
        _registry.Setup(r => r.GetAllSupportedProviderTypes()).Returns(["opencode", "pi", "claude-code", "copilot"]);
        _registry.Setup(r => r.GetModelSupport(It.IsAny<string>()))
            .Returns((string type) => DeclaredModelSupport.For(type));
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly RecordingProvisioner _provisioner = new();

    private AiProvidersController CreateController()
        => new(
            Mock.Of<IAIProviderService>(),
            _registry.Object,
            _db,
            new ProviderStore(_db),
            new InteractiveProviderSessionService(NullLogger<InteractiveProviderSessionService>.Instance),
            _provisioner);

    /// <summary>Captures the provider types the controller asks to provision.</summary>
    private sealed class RecordingProvisioner : IManagedAgentProvisioner
    {
        public List<string?> Requested { get; } = new();
        public void EnsureInstalledForProviderType(string? providerType) => Requested.Add(providerType);
    }

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
    public async Task Create_accepts_copilot_with_empty_url_and_model()
    {
        var controller = CreateController();

        var result = await controller.Create(new AiProviderDto
        {
            Name = "copilot-sub",
            Type = "copilot",
            BaseUrl = string.Empty,
            Model = string.Empty,
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        var json = System.Text.Json.JsonSerializer.Serialize(created.Value);
        Assert.Contains("copilot", json);
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

    [Theory]
    [InlineData("pi")]
    [InlineData("opencode")]
    // The API accepts a type in any case, so a mixed-case one still has to be
    // held to its adapter's Required declaration rather than slipping through.
    [InlineData("Pi")]
    [InlineData("OpenCode")]
    [InlineData("OPENCODE")]
    public async Task Create_rejects_a_required_model_adapter_without_a_model(string type)
    {
        var controller = CreateController();

        var result = await controller.Create(new AiProviderDto
        {
            Name = $"{type}-no-model",
            Type = type,
            BaseUrl = "https://api.example.com",
            Model = string.Empty,
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Model is required", System.Text.Json.JsonSerializer.Serialize(badRequest.Value));
    }

    [Fact]
    public async Task Create_then_Update_keeps_a_claude_code_providers_model()
    {
        var controller = CreateController();

        var created = Assert.IsType<CreatedAtActionResult>(await controller.Create(new AiProviderDto
        {
            Name = "claude-opus",
            Type = "claude-code",
            BaseUrl = string.Empty,
            Model = "opus",
        }));
        var id = (Guid)created.Value!.GetType().GetProperty("id")!.GetValue(created.Value)!;

        await controller.Update(id.ToString(), new AiProviderDto
        {
            Name = "claude-opus renamed",
            Type = "claude-code",
            BaseUrl = string.Empty,
            Model = "sonnet",
        });

        Assert.Equal("sonnet", (await _db.AiProviders.FindAsync(id))!.Model);
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

    // ── Provider tags ───────────────────────────────────────────────────────

    /// <summary>
    /// A controller over a cleared change tracker: each call sees the database
    /// the way a fresh request would, not entities an earlier call left tracked.
    /// </summary>
    private AiProvidersController Request()
    {
        _db.ChangeTracker.Clear();
        return CreateController();
    }

    private static AiProviderDto TagDto(string name, IEnumerable<string>? tags) => new()
    {
        Name = name,
        Type = "claude-code",
        BaseUrl = string.Empty,
        Model = string.Empty,
        Tags = tags?.ToList(),
    };

    private static System.Text.Json.JsonElement Body(IActionResult result)
        => System.Text.Json.JsonSerializer.SerializeToElement(((ObjectResult)result).Value);

    private static string[] TagsOf(System.Text.Json.JsonElement provider)
        => provider.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray();

    private static string[] TagsOf(IActionResult result) => TagsOf(Body(result));

    private static string ErrorOf(IActionResult result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        return System.Text.Json.JsonSerializer.SerializeToElement(bad.Value).GetProperty("error").GetString()!;
    }

    private async Task<Guid> CreateTaggedAsync(string name, params string[] tags)
    {
        var result = Assert.IsType<CreatedAtActionResult>(
            await Request().Create(TagDto(name, tags.Length == 0 ? null : tags)));
        return Body(result).GetProperty("id").GetGuid();
    }

    private async Task<string[]> StoredTagsAsync(Guid id)
        => TagsOf(await Request().GetById(id.ToString()));

    [Fact]
    public async Task Every_provider_response_carries_its_tags_in_case_insensitive_order()
    {
        var created = await Request().Create(TagDto("tagged", ["Zeta", "alpha", "beta"]));
        Assert.Equal(["alpha", "beta", "Zeta"], TagsOf(created));
        var id = Body(created).GetProperty("id").GetGuid();

        var untagged = await Request().Create(TagDto("untagged", null));
        Assert.Empty(TagsOf(untagged));

        var all = Body(await Request().GetAll()).EnumerateArray().ToList();
        Assert.Equal(["alpha", "beta", "Zeta"], TagsOf(all.Single(p => p.GetProperty("name").GetString() == "tagged")));
        Assert.Empty(TagsOf(all.Single(p => p.GetProperty("name").GetString() == "untagged")));

        Assert.Equal(["alpha", "beta", "Zeta"], TagsOf(await Request().GetById(id.ToString())));
        Assert.Equal(["alpha", "beta", "Zeta"], TagsOf(await Request().SetDefault(id.ToString())));

        // Omitting tags on update leaves them as they were.
        var updated = await Request().Update(id.ToString(), TagDto("tagged renamed", null));
        Assert.Equal(["alpha", "beta", "Zeta"], TagsOf(updated));
        Assert.Equal(["alpha", "beta", "Zeta"], await StoredTagsAsync(id));
    }

    [Fact]
    public async Task Tags_are_trimmed_blanks_dropped_and_case_duplicates_collapsed_to_the_first_spelling()
    {
        var created = await Request().Create(TagDto("p", [" QA ", "", "   ", "qa", "Fast", "FAST"]));

        Assert.Equal(["Fast", "QA"], TagsOf(created));
        Assert.Equal(["Fast", "QA"], await StoredTagsAsync(Body(created).GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Tags_at_the_limits_are_accepted()
    {
        var longest = new string('x', 64);
        var thirtyTwo = Enumerable.Range(0, 31).Select(i => $"t{i}").Append(longest).ToList();

        var id = await CreateTaggedAsync("p", [.. thirtyTwo]);

        var stored = await StoredTagsAsync(id);
        Assert.Equal(32, stored.Length);
        Assert.Contains(longest, stored);
    }

    public static TheoryData<string[], string?> InvalidTags => new()
    {
        { ["ok", new string('y', 65)], new string('y', 65) },
        { ["ok", "qa,fast"], "qa,fast" },
        { Enumerable.Range(0, 33).Select(i => $"t{i}").ToArray(), null },
    };

    [Theory]
    [MemberData(nameof(InvalidTags))]
    public async Task Create_with_an_invalid_tag_set_is_rejected_and_saves_nothing(string[] tags, string? named)
    {
        var result = await Request().Create(TagDto("p", tags));

        var error = ErrorOf(result);
        if (named is not null) Assert.Contains(named, error);
        _db.ChangeTracker.Clear();
        Assert.Empty(_db.AiProviders);
    }

    [Theory]
    [MemberData(nameof(InvalidTags))]
    public async Task Update_with_an_invalid_tag_set_is_rejected_and_saves_nothing(string[] tags, string? named)
    {
        var id = await CreateTaggedAsync("before", "keep");

        var result = await Request().Update(id.ToString(), TagDto("after", tags));

        var error = ErrorOf(result);
        if (named is not null) Assert.Contains(named, error);
        Assert.Equal(["keep"], await StoredTagsAsync(id));
        _db.ChangeTracker.Clear();
        Assert.Equal("before", (await _db.AiProviders.FindAsync(id))!.Name);
    }

    [Fact]
    public async Task Update_replaces_the_tag_set_exactly_and_an_empty_list_removes_all()
    {
        var id = await CreateTaggedAsync("p", "one", "two");

        var replaced = await Request().Update(id.ToString(), TagDto("p", ["two", "three"]));
        Assert.Equal(["three", "two"], TagsOf(replaced));
        Assert.Equal(["three", "two"], await StoredTagsAsync(id));

        var cleared = await Request().Update(id.ToString(), TagDto("p", []));
        Assert.Empty(TagsOf(cleared));
        Assert.Empty(await StoredTagsAsync(id));
    }

    [Fact]
    public async Task Saving_a_tag_another_provider_holds_moves_it_and_leaves_its_other_tags()
    {
        var a = await CreateTaggedAsync("A", "QA", "Fast", "Thinking");

        // On create, compared case-insensitively.
        var b = await CreateTaggedAsync("B", "qa");
        Assert.Equal(["qa"], await StoredTagsAsync(b));
        Assert.Equal(["Fast", "Thinking"], await StoredTagsAsync(a));

        // On update.
        var moved = await Request().Update(b.ToString(), TagDto("B", ["qa", "FAST"]));
        Assert.Equal(["FAST", "qa"], TagsOf(moved));
        Assert.Equal(["Thinking"], await StoredTagsAsync(a));
    }

    [Fact]
    public async Task Changing_only_the_case_of_a_tag_on_the_same_provider_keeps_the_new_spelling()
    {
        var id = await CreateTaggedAsync("p", "qa", "fast");

        var updated = await Request().Update(id.ToString(), TagDto("p", ["QA", "fast"]));

        Assert.Equal(["fast", "QA"], TagsOf(updated));
        Assert.Equal(["fast", "QA"], await StoredTagsAsync(id));
    }
}
