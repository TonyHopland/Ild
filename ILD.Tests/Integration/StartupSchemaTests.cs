namespace ILD.Tests.Integration;

public class StartupSchemaTests
{
    [Fact]
    public async Task Without_a_connection_string_the_api_creates_its_schema_on_an_empty_database()
    {
        await using var factory = new ApiFactory(emptyDatabase: true);

        var client = await factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/v1/looptemplates", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
