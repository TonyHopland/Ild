using System.Runtime.Versioning;
using System.Text.Json;
using ILD.Core.Services.Implementations;
using static ILD.Tests.Integration.StorageApiTests;

namespace ILD.Tests.Integration;

/// <summary>
/// A category with an entry that was not measured: its total is what was
/// measured, and it says it is incomplete rather than passing for the whole.
/// The test clears the process-wide scratch-root variable, hence the collection.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
[SupportedOSPlatform("linux")]
public class StorageOverviewNotMeasuredTests
{
    [Fact]
    public async Task Temp_is_incomplete_when_the_scratch_root_is_not_measured()
    {
        var previous = Environment.GetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar);
        Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, null);
        try
        {
            await using var factory = new ApiFactory();
            var client = await factory.CreateAuthenticatedClientAsync();

            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/v1/storage", TestContext.Current.CancellationToken));

            var temp = Category(doc, "temp");
            Assert.Equal(JsonValueKind.Null, Entry(temp, "scratch").GetProperty("bytes").ValueKind);
            Assert.True(temp.GetProperty("incomplete").GetBoolean());
            Assert.Equal(Entries(temp).Sum(e => e.GetProperty("bytes").ValueKind == JsonValueKind.Number ? Bytes(e) : 0), Bytes(temp));
            Assert.False(Category(doc, "chat-sessions").GetProperty("incomplete").GetBoolean());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentIsolation.ScratchRootEnvVar, previous);
        }
    }
}
