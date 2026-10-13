using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// Global variables through the real API: instance-wide named values a signed-in
/// user manages (and the agent token may not touch), the length limit setting
/// that bounds them, and the loop templates that use each one.
/// </summary>
public class GlobalVariablesApiTests
{
    private const string Url = "/api/v1/global-variables";
    private const string LimitUrl = "/api/v1/settings/globalVariables.maxLength";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpClient AgentClient(ApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<ILD.Api.Configuration.AgentAuthTokenProvider>().Token;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<JsonElement> CreateAsync(HttpClient client, string name, string value)
    {
        var response = await client.PostAsJsonAsync(Url, new { name, value }, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"create {name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        return await Json(response);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync(Url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).EnumerateArray().ToArray();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response, string because)
    {
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"{because}: expected 400, got {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        var body = await Json(response);
        Assert.True(body.TryGetProperty("error", out var error) && !string.IsNullOrWhiteSpace(error.GetString()),
            $"{because}: the 400 carries no reason");
        return error.GetString()!;
    }

    private static async Task SetLimitAsync(HttpClient client, int limit)
    {
        using var response = await client.PutAsJsonAsync(LimitUrl, new { value = limit.ToString() }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_agent_token_is_refused_at_every_endpoint()
    {
        await using var factory = new ApiFactory();
        var user = await factory.CreateAuthenticatedClientAsync();
        var id = (await CreateAsync(user, "rules", "be brief")).GetProperty("id").GetString()!;
        var agent = AgentClient(factory);

        var responses = new[]
        {
            await agent.GetAsync(Url, Ct),
            await agent.PostAsJsonAsync(Url, new { name = "agent_made", value = "x" }, Ct),
            await agent.PutAsJsonAsync($"{Url}/{id}", new { value = "changed by agent" }, Ct),
            await agent.DeleteAsync($"{Url}/{id}", Ct),
        };

        foreach (var response in responses)
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var only = Assert.Single(await ListAsync(user));
        Assert.Equal("be brief", only.GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_variable_is_created_listed_by_name_edited_and_deleted()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var created = await CreateAsync(client, "  zeta  ", "last");
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("zeta", created.GetProperty("name").GetString());
        Assert.Equal("last", created.GetProperty("value").GetString());
        Assert.True(created.TryGetProperty("updatedAt", out _));
        Assert.Empty(created.GetProperty("usedBy").EnumerateArray());
        await CreateAsync(client, "alpha", "");
        await CreateAsync(client, "mid", "middle");

        var listed = await ListAsync(client);
        Assert.Equal(new[] { "alpha", "mid", "zeta" }, listed.Select(v => v.GetProperty("name").GetString()));
        Assert.Equal("", listed[0].GetProperty("value").GetString());
        Assert.All(listed, v => Assert.Empty(v.GetProperty("usedBy").EnumerateArray()));

        // The name is the variable's identity; an edit changes only the value.
        var edited = await client.PutAsJsonAsync($"{Url}/{id}", new { name = "renamed", value = "changed" }, Ct);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("changed", (await Json(edited)).GetProperty("value").GetString());
        var zeta = (await ListAsync(client)).Single(v => v.GetProperty("id").GetString() == id);
        Assert.Equal("zeta", zeta.GetProperty("name").GetString());
        Assert.Equal("changed", zeta.GetProperty("value").GetString());

        var emptied = await client.PutAsJsonAsync($"{Url}/{id}", new { value = "" }, Ct);
        Assert.Equal(HttpStatusCode.OK, emptied.StatusCode);

        var deleted = await client.DeleteAsync($"{Url}/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(new[] { "alpha", "mid" }, (await ListAsync(client)).Select(v => v.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task An_edit_stamps_the_time_it_was_made()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = Guid.Parse((await CreateAsync(client, "rules", "old")).GetProperty("id").GetString()!);
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Set<GlobalVariable>().SingleAsync(g => g.Id == id, Ct);
            row.UpdatedAt = longAgo;
            await db.SaveChangesAsync(Ct);
        }

        var response = await client.PutAsJsonAsync($"{Url}/{id}", new { value = "new" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = Assert.Single(await ListAsync(client));
        Assert.True(listed.GetProperty("updatedAt").GetDateTime() > longAgo.AddDays(1),
            $"updatedAt was not stamped: {listed.GetProperty("updatedAt")}");
    }

    [Fact]
    public async Task An_unknown_id_is_404_for_edit_and_delete()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Url}/{Guid.NewGuid()}", new { value = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Url}/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_bad_or_taken_name_or_a_missing_value_is_refused_with_a_reason_and_nothing_is_stored()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        var id = (await CreateAsync(client, "review_rules", "x")).GetProperty("id").GetString()!;

        var cases = new (object Body, string Because)[]
        {
            (new { name = "", value = "x" }, "empty name"),
            (new { name = "   ", value = "x" }, "blank name"),
            (new { name = "9lives", value = "x" }, "name not starting with a letter"),
            (new { name = "bad-name", value = "x" }, "name with a hyphen"),
            (new { name = "Global.rules", value = "x" }, "name with a dot"),
            (new { name = "a" + new string('b', 128), value = "x" }, "name over 128 characters"),
            (new { name = "REVIEW_RULES", value = "x" }, "name taken, ignoring case"),
            (new { name = " review_rules ", value = "x" }, "name taken after trimming"),
            (new { name = "fresh" }, "no value"),
        };
        foreach (var (body, because) in cases)
            await ErrorOf(await client.PostAsJsonAsync(Url, body, Ct), because);
        await ErrorOf(await client.PutAsJsonAsync($"{Url}/{id}", new { }, Ct), "edit without a value");

        var only = Assert.Single(await ListAsync(client));
        Assert.Equal("x", only.GetProperty("value").GetString());
        await CreateAsync(client, "a" + new string('b', 127), "x");
    }

    [Fact]
    public async Task The_length_limit_defaults_to_4000_and_takes_only_integers_from_1_to_8192()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        async Task<string> Stored() => (await client.GetFromJsonAsync<JsonElement>(LimitUrl, Ct)).GetProperty("value").GetString()!;

        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/v1/settings", Ct);
        Assert.Equal("4000", all!.Single(s => s.GetProperty("key").GetString() == "globalVariables.maxLength").GetProperty("value").GetString());
        Assert.Equal("4000", await Stored());

        foreach (var valid in new[] { "1", "8192", "500" })
        {
            using var stored = await client.PutAsJsonAsync(LimitUrl, new { value = valid }, Ct);
            Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
            Assert.Equal(valid, await Stored());
        }

        foreach (var invalid in new[] { "0", "8193", "-1", "four thousand", "", "2.5" })
        {
            using var refused = await client.PutAsJsonAsync(LimitUrl, new { value = invalid }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("500", await Stored());
        }
    }

    [Fact]
    public async Task A_value_over_the_default_limit_is_refused_stating_its_length_and_the_limit()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();

        var error = await ErrorOf(await client.PostAsJsonAsync(Url, new { name = "rules", value = new string('v', 4001) }, Ct),
            "create over the default limit");
        Assert.Contains("4001", error);
        Assert.Contains("4000", error);
        Assert.Empty(await ListAsync(client));

        await CreateAsync(client, "rules", new string('v', 4000));
    }

    [Fact]
    public async Task Lowering_the_limit_keeps_existing_values_but_the_next_edit_must_fit()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        await SetLimitAsync(client, 12);

        var refused = await ErrorOf(await client.PostAsJsonAsync(Url, new { name = "rules", value = new string('v', 13) }, Ct),
            "create over a limit of 12");
        Assert.Contains("13", refused);
        Assert.Contains("12", refused);
        var id = (await CreateAsync(client, "rules", "twelve chars")).GetProperty("id").GetString()!;

        await SetLimitAsync(client, 5);

        Assert.Equal("twelve chars", Assert.Single(await ListAsync(client)).GetProperty("value").GetString());
        var error = await ErrorOf(await client.PutAsJsonAsync($"{Url}/{id}", new { value = "sixsix" }, Ct),
            "edit over the lowered limit");
        Assert.Contains("6", error);
        Assert.Contains("5", error);
        Assert.Equal("twelve chars", Assert.Single(await ListAsync(client)).GetProperty("value").GetString());

        var fits = await client.PutAsJsonAsync($"{Url}/{id}", new { value = "fives" }, Ct);
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
        Assert.Equal("fives", Assert.Single(await ListAsync(client)).GetProperty("value").GetString());
    }

    // ── Templates using a variable ───────────────────────────────────────

    private static Guid SeedTemplate(ApiFactory factory, string name, bool archived, params string?[] promptPerVersion)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = new LoopTemplate { Id = Guid.NewGuid(), Name = name, IsArchived = archived };
        db.LoopTemplates.Add(template);
        for (var i = 0; i < promptPerVersion.Length; i++)
        {
            var version = new LoopTemplateVersion
            {
                Id = Guid.NewGuid(),
                LoopTemplateId = template.Id,
                VersionNumber = i + 1,
                CreatedAt = DateTime.UtcNow.AddMinutes(i - promptPerVersion.Length),
            };
            db.LoopTemplateVersions.Add(version);
            db.LoopNodes.Add(new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = version.Id,
                NodeType = NodeType.Start,
                Label = "Start",
            });
            if (promptPerVersion[i] is { } prompt)
                db.LoopNodes.Add(new LoopNode
                {
                    Id = Guid.NewGuid(),
                    LoopTemplateVersionId = version.Id,
                    NodeType = NodeType.AI,
                    Label = "Writer",
                    Config = JsonSerializer.Serialize(new { prompt }),
                });
        }
        db.SaveChanges();
        return template.Id;
    }

    private static (string Id, string Name)[] UsedBy(JsonElement variable)
        => variable.GetProperty("usedBy").EnumerateArray()
            .Select(t => (t.GetProperty("id").GetString()!, t.GetProperty("name").GetString()!))
            .OrderBy(t => t.Item2, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task Each_variable_lists_the_live_templates_whose_latest_version_uses_it()
    {
        await using var factory = new ApiFactory();
        var client = await factory.CreateAuthenticatedClientAsync();
        await CreateAsync(client, "Rules", "be brief");
        await CreateAsync(client, "style", "plain");

        // A full page of templates that use nothing sorts ahead of the ones that do.
        for (var i = 0; i < 100; i++)
            SeedTemplate(factory, $"a-filler-{i:D3}", archived: false, "Nothing global here");
        var exact = SeedTemplate(factory, "m-exact", archived: false, "Follow {{Global.Rules}}");
        var spaced = SeedTemplate(factory, "m-spaced-other-case", archived: false, "Follow {{ Global.rules }} and {{Global.style}}");
        var nowUses = SeedTemplate(factory, "m-uses-only-latest", archived: false, "Nothing global here", "Follow {{Global.RULES}}");
        SeedTemplate(factory, "m-used-only-earlier", archived: false, "Follow {{Global.Rules}}", "Nothing global here");
        SeedTemplate(factory, "m-archived", archived: true, "Follow {{Global.Rules}}");
        SeedTemplate(factory, "m-loop-variable", archived: false, "Follow {{Var.Rules}}");
        SeedTemplate(factory, "m-longer-name", archived: false, "Follow {{Global.Rules2}}");
        var last = SeedTemplate(factory, "zz-last", archived: false, "Follow {{Global.Rules}}");

        var listed = await ListAsync(client);

        var rules = listed.Single(v => v.GetProperty("name").GetString() == "Rules");
        Assert.Equal(new[]
        {
            (exact.ToString(), "m-exact"),
            (spaced.ToString(), "m-spaced-other-case"),
            (nowUses.ToString(), "m-uses-only-latest"),
            (last.ToString(), "zz-last"),
        }, UsedBy(rules));
        var style = listed.Single(v => v.GetProperty("name").GetString() == "style");
        Assert.Equal(new[] { (spaced.ToString(), "m-spaced-other-case") }, UsedBy(style));
    }
}
