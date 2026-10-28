using System.Net;
using System.Net.Http.Json;
using ILD.Data.Entities;
using ILD.Data.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// The reads behind the global-variable views stay flat however many templates
/// and parked runs there are: one query for the templates' latest nodes, and one
/// node read per template version a page of runs shares.
/// </summary>
// A host per test: each test counts every SQL statement the host runs.
public class GlobalVariableReadsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int QueriesOnNodes(SqlCapture sql) => sql.Commands.Count(c => c.Contains("\"LoopNodes\""));

    private static int VersionNodeReads(SqlCapture sql) => sql.Commands.Count(c => c.Contains("FROM \"LoopNodes\""));

    [Fact]
    public async Task Listing_the_variables_reads_every_templates_latest_nodes_in_one_query()
    {
        var sql = new SqlCapture();
        await using var factory = new ApiFactory(configureServices: sql.Register);
        var client = await factory.CreateAuthenticatedClientAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 30; i++)
            {
                var template = new LoopTemplate { Id = Guid.NewGuid(), Name = $"t-{i:D2}" };
                db.LoopTemplates.Add(template);
                for (var v = 1; v <= 2; v++)
                {
                    var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = v, CreatedAt = DateTime.UtcNow };
                    db.LoopTemplateVersions.Add(version);
                    db.LoopNodes.Add(new LoopNode
                    {
                        Id = Guid.NewGuid(),
                        LoopTemplateVersionId = version.Id,
                        NodeType = NodeType.AI,
                        Label = "Writer",
                        Config = v == 2 ? "{\"prompt\":\"{{Global.rules}}\"}" : "{\"prompt\":\"plain\"}",
                    });
                }
            }
            db.SaveChanges();
        }
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/v1/global-variables", new { name = "rules", value = "x" }, Ct)).StatusCode);

        var before = QueriesOnNodes(sql);
        var listed = await client.GetFromJsonAsync<System.Text.Json.JsonElement[]>("/api/v1/global-variables", Ct);

        Assert.Equal(30, listed!.Single().GetProperty("usedBy").GetArrayLength());
        Assert.Equal(1, QueriesOnNodes(sql) - before);
    }

    [Fact]
    public async Task Runs_parked_for_globals_on_one_version_share_one_read_of_its_nodes()
    {
        var sql = new SqlCapture();
        await using var factory = new ApiFactory(configureServices: sql.Register);
        var client = await factory.CreateAuthenticatedClientAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var template = new LoopTemplate { Id = Guid.NewGuid(), Name = "parked" };
            var version = new LoopTemplateVersion { Id = Guid.NewGuid(), LoopTemplateId = template.Id, VersionNumber = 1, CreatedAt = DateTime.UtcNow };
            var node = new LoopNode
            {
                Id = Guid.NewGuid(),
                LoopTemplateVersionId = version.Id,
                NodeType = NodeType.AI,
                Label = "Writer",
                Config = "{\"prompt\":\"{{Global.missing}}\"}",
            };
            db.LoopTemplates.Add(template);
            db.LoopTemplateVersions.Add(version);
            db.LoopNodes.Add(node);
            for (var i = 0; i < 3; i++)
                db.LoopRuns.Add(new LoopRun
                {
                    Id = Guid.NewGuid(),
                    WorkItemId = $"wi-{i}",
                    LoopTemplateVersionId = version.Id,
                    Status = LoopRunStatus.WaitingHuman,
                    IsHalted = true,
                    HaltReason = HaltReason.GlobalVariableMissing,
                    CurrentNodeId = node.Id,
                    StartedAt = DateTime.UtcNow,
                });
            db.SaveChanges();
        }

        var before = VersionNodeReads(sql);
        var runs = await client.GetFromJsonAsync<System.Text.Json.JsonElement[]>("/api/v1/loopruns", Ct);

        Assert.All(runs!, r => Assert.Equal("missing", r.GetProperty("missingGlobalVariables")[0].GetString()));
        Assert.Equal(1, VersionNodeReads(sql) - before);
    }
}
