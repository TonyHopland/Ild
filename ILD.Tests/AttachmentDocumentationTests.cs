namespace ILD.Tests;

/// <summary>
/// The two attachment settings are environment variables on two containers, so
/// an operator who cannot find them in the docs and the compose file cannot
/// change them at all.
/// </summary>
public class AttachmentDocumentationTests
{
    private const string PerFileVariable = "ILD_MAX_ATTACHMENT_MB";
    private const string TotalVariable = "ILD_MAX_ATTACHMENTS_TOTAL_MB";

    private static string LineMentioning(string relativePath, string needle)
    {
        var line = RepositoryFiles.ReadAllLines(relativePath).FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal));
        Assert.True(line != null, $"{relativePath} does not mention {needle}");
        return line!;
    }

    [Fact]
    public void The_configuration_reference_documents_both_variables_with_their_defaults_and_unit()
    {
        var perFile = LineMentioning("docs/configuration.md", PerFileVariable);
        var total = LineMentioning("docs/configuration.md", TotalVariable);

        Assert.Contains("25", perFile, StringComparison.Ordinal);
        Assert.Contains("250", total, StringComparison.Ordinal);
        Assert.Contains("MB", RepositoryFiles.ReadAllText("docs/configuration.md"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_env_file_carries_both_variables()
    {
        Assert.Contains("25", LineMentioning(".env.example", PerFileVariable), StringComparison.Ordinal);
        Assert.Contains("250", LineMentioning(".env.example", TotalVariable), StringComparison.Ordinal);
    }

    [Fact]
    public void Both_services_in_the_compose_stack_carry_both_variables()
    {
        var services = ComposeServices();

        foreach (var service in new[] { "ild", "workitem-server" })
        {
            Assert.True(services.ContainsKey(service), $"docker-compose.yml has no '{service}' service");
            Assert.Contains(PerFileVariable, services[service], StringComparison.Ordinal);
            Assert.Contains(TotalVariable, services[service], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_api_reference_covers_the_new_routes()
    {
        var api = RepositoryFiles.ReadAllText("docs/api.md");

        Assert.Contains("/api/v1/workitems/{id}/attachments", api, StringComparison.Ordinal);
        Assert.Contains("/api/v1/settings/attachments", api, StringComparison.Ordinal);
    }

    [Fact]
    public void The_changelog_says_what_is_different_now()
    {
        var changelog = RepositoryFiles.ReadAllText("CHANGELOG.md");
        var afterHeading = changelog[(changelog.IndexOf("## [Unreleased]", StringComparison.Ordinal) + 1)..];
        var nextRelease = afterHeading.IndexOf("\n## [", StringComparison.Ordinal);
        var unreleased = nextRelease < 0 ? afterHeading : afterHeading[..nextRelease];

        Assert.Contains("attach", unreleased, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The environment block of each top-level compose service, keyed by service name.</summary>
    private static Dictionary<string, string> ComposeServices()
    {
        var services = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var inServices = false;
        foreach (var line in RepositoryFiles.ReadAllLines("docker-compose.yml"))
        {
            if (line.StartsWith("services:", StringComparison.Ordinal)) { inServices = true; continue; }
            if (line.Length > 0 && !char.IsWhiteSpace(line[0])) { inServices = false; current = null; continue; }
            if (!inServices) continue;

            if (line.Length > 2 && line.StartsWith("  ", StringComparison.Ordinal)
                && !char.IsWhiteSpace(line[2]) && line.TrimEnd().EndsWith(':'))
            {
                current = line.Trim().TrimEnd(':');
                services[current] = string.Empty;
                continue;
            }
            if (current != null) services[current] += line + "\n";
        }
        return services;
    }
}
