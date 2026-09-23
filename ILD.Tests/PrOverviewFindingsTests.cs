using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// The reviewer changed its format and the parser did not notice.
///
/// PR #161's review 5270659986 carried three findings and not one inline
/// comment: they sat in the body under "Previously missed (3)", each in its own
/// collapsible. The parser looked for a "Suppressed comments" heading, found
/// none, and reported nothing — so the whole review reached the loop as one
/// block of prose with no ids, no fingerprints and none of the per-finding
/// throttling every other item gets.
///
/// Pinned against that real body, kept verbatim under Fixtures/, for the same
/// reason the older shapes are: this is not a format anyone promised, and a
/// parser tested against a shape we imagined would have passed all along.
/// </summary>
public class PrOverviewFindingsTests
{
    /// <summary>PR #161, review 5270659986 — ccr-overview-v2, three body-only findings, no heading.</summary>
    private const string CurrentOverview = "pr161-review-5270659986.md";

    private static string Fixture(string name)
        => RepositoryFiles.ReadAllText(Path.Combine("ILD.Tests", "Fixtures", name));

    private static RemotePrReviewSummary Review(string? body, string id = "5270659986") =>
        new(id, "COMMENTED", body, "9ccc844", new DateTime(2026, 9, 21, 19, 1, 39, DateTimeKind.Utc), "Copilot", false);

    [Fact]
    public void The_real_review_that_hid_three_findings_under_a_renamed_section_yields_all_three()
    {
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(CurrentOverview)));

        Assert.Equal(
            new[]
            {
                ("ILD.Core/Services/Implementations/Executors/PRNodeExecutor.cs", 327),
                ("ILD.Core/Services/Implementations/RemoteProviders/AzureDevOpsRemoteGitProviderAdapter.cs", 285),
                ("ILD.Core/Services/Remote/PrReviewService.cs", 185),
            },
            items.Select(i => (i.Path!, i.Line!.Value)).ToArray());
    }

    [Fact]
    public void Each_one_carries_its_headline_and_the_prose_under_it()
    {
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(CurrentOverview)));

        var refusal = items.Single(i => i.Path!.EndsWith("PRNodeExecutor.cs", StringComparison.Ordinal));
        // The headline is the only place the finding says what it wants done.
        Assert.StartsWith("Restore delivery state when provider refusal leaves item unqueued", refusal.Body, StringComparison.Ordinal);
        Assert.Contains("ClaimQueuedWritesAsync", refusal.Body, StringComparison.Ordinal);
        Assert.All(items, i => Assert.True(i.Body.Trim().Length > 80, $"{i.Path}:{i.Line} came back with no usable body"));
    }

    [Fact]
    public void The_severity_badge_never_reaches_the_agent()
    {
        // Each headline opens with a <picture> of three <source> elements. Left
        // in, the batch is mostly image URLs and the cap eats the prose.
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(CurrentOverview)));

        Assert.All(items, i => Assert.DoesNotContain("<picture", i.Body, StringComparison.OrdinalIgnoreCase));
        Assert.All(items, i => Assert.DoesNotContain("githubassets.com", i.Body, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_finding_that_did_become_a_thread_is_not_read_out_of_the_body_as_well()
    {
        // "Open (2)" and "Resolved since last review (2)" are collapsibles too,
        // listing findings that have comments and ids of their own. Reading them
        // here would deliver each of those twice, once with no id to answer on.
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(CurrentOverview)));

        Assert.DoesNotContain(items, i => i.Body.Contains("Seed delivery state only after edge selection", StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Body.Contains("discussion_r", StringComparison.Ordinal));
        // …nor is "Files not reviewed" a finding about a generated file.
        Assert.DoesNotContain(items, i => i.Path!.Contains("Designer.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_invisible_spaces_github_breaks_a_path_with_are_taken_out()
    {
        // GitHub wraps a long path by inserting U+200B. Left in, the same
        // finding fingerprints differently depending on where the line broke,
        // and the path never matches a file anyone can open.
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(CurrentOverview)));

        Assert.All(items, i => Assert.DoesNotContain('​', i.Path!));
        Assert.All(items, i => Assert.DoesNotContain('​', i.Body));
    }

    [Fact]
    public void The_wrapper_written_on_one_line_is_read_too()
    {
        // The shape Copilot's own accepted format documents, and the one the
        // reviewer raised: <details><summary>…</summary> with no line break.
        var body = "<details><summary><strong>Previously missed (1)</strong></summary>\n"
            + "<details><summary>Name the thing it is about</summary>\n\n"
            + "`src/A.cs:57`\n\n"
            + "The prose that makes it actionable.\n</details>\n</details>";

        var item = Assert.Single(PrReviewBodyParser.Suppressed(Review(body)));

        Assert.Equal("src/A.cs", item.Path);
        Assert.Equal(57, item.Line);
        Assert.Contains("Name the thing it is about", item.Body, StringComparison.Ordinal);
        Assert.Contains("makes it actionable", item.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_heading_the_older_reviews_used_still_parses()
    {
        // The reason both readers exist: PR #158's "### Suppressed comments (8)"
        // has to keep working, whatever the current overview looks like.
        var body = "<details>\n<summary>Suppressed comments (1)</summary>\n\n"
            + "**src/Old.cs:12**\n* still found by the older reader\n</details>";

        var item = Assert.Single(PrReviewBodyParser.Suppressed(Review(body)));

        Assert.Equal("src/Old.cs", item.Path);
        Assert.Equal(12, item.Line);
    }

    [Fact]
    public void One_finding_written_in_both_shapes_is_still_one_finding()
    {
        var body = "<details>\n<summary>Suppressed comments (1)</summary>\n\n"
            + "**src/A.cs:57**\n* the very same words\n</details>\n"
            + "<details>\n<summary>src/A.cs:57</summary>\n\n`src/A.cs:57`\n\nthe very same words\n</details>";

        var items = PrReviewBodyParser.Suppressed(Review(body));

        Assert.Equal(2, items.Count(i => i.Path == "src/A.cs"));
        // Different prose, so genuinely two — the dedupe is on what they say,
        // and neither reader may drop a finding the other did not find.
        Assert.All(items, i => Assert.Equal(57, i.Line));
    }

    [Fact]
    public void A_collapsible_that_is_not_a_finding_yields_nothing_and_never_throws()
    {
        Assert.Empty(PrReviewBodyParser.Suppressed(Review(
            "<details>\n<summary>Files not reviewed (2)</summary>\n\n* **a/b.cs**: Generated file\n</details>")));
        Assert.Empty(PrReviewBodyParser.Suppressed(Review("<details>\n<summary></summary>\n\n`src/A.cs:1`\n</details>")));
        Assert.Empty(PrReviewBodyParser.Suppressed(Review("<details>\n<summary>Unclosed")));
        // A line number no int can hold is not a location.
        Assert.Empty(PrReviewBodyParser.Suppressed(Review(
            "<details>\n<summary>Too big</summary>\n\n`src/A.cs:99999999999999999999`\n\nprose\n</details>")));
    }
}
