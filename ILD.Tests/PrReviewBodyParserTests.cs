using ILD.Core.Services.Implementations.RemoteProviders;
using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// Most of a Copilot review never becomes a thread: on PR #158's first review
/// eight findings lived only inside the review body, and the loop answered the
/// four it could see. The parser is what makes those readable, so it is pinned
/// against the real bodies (kept verbatim under Fixtures/) rather than against
/// a shape we imagine the reviewer emits.
/// </summary>
public class PrReviewBodyParserTests
{
    private static string Fixture(string name)
        => RepositoryFiles.ReadAllText(Path.Combine("ILD.Tests", "Fixtures", name));

    /// <summary>PR #158, review 5250768235 — "### Suppressed comments (8)" inside a Review details block.</summary>
    private const string ReviewDetailsShape = "pr158-review-5250768235.md";

    /// <summary>PR #153, review 5222030374 — &lt;details&gt;&lt;summary&gt;Suppressed comments (1)&lt;/summary&gt;, and truncated.</summary>
    private const string DetailsSummaryShape = "pr153-review-5222030374.md";

    /// <summary>PR #158, review 5255961714 — a complete review with no suppressed section at all.</summary>
    private const string NoSuppressedSection = "pr158-review-5255961714.md";

    private static RemotePrReviewSummary Review(string? body, string id = "5250768235", string head = "7e932b3d") =>
        new(id, "COMMENTED", body, head, new DateTime(2026, 9, 18, 17, 34, 45, DateTimeKind.Utc), "Copilot", false);

    [Fact]
    public void The_real_review_that_hid_eight_findings_yields_all_eight_with_their_files_and_lines()
    {
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(ReviewDetailsShape)));

        Assert.Equal(8, items.Count);
        Assert.Equal(
            new[]
            {
                ("frontend/src/components/workitem-v2/AttachmentList.tsx", 57),
                ("frontend/src/components/workitem-v2/AttachmentPicker.tsx", 64),
                ("frontend/src/components/workitem-v2/AttachmentPicker.tsx", 36),
                ("frontend/src/components/workitem-v2/WorkItemModalV2.tsx", 92),
                ("frontend/src/components/workitem-v2/useAttachmentStaging.ts", 157),
                ("frontend/src/components/workitem-v2/useAttachmentStaging.ts", 94),
                ("frontend/src/components/workitem-v2/useWorkItemDetail.ts", 607),
                ("frontend/src/utils/attachments.ts", 30),
            },
            items.Select(i => (i.Path!, i.Line!.Value)).ToArray());
    }

    [Fact]
    public void Each_suppressed_finding_carries_the_prose_that_makes_it_actionable()
    {
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(ReviewDetailsShape)));

        var note = items.Single(i => i.Path!.EndsWith("utils/attachments.ts", StringComparison.Ordinal));
        Assert.Contains("attachedNote", note.Body, StringComparison.Ordinal);
        Assert.Contains("preserve the original text", note.Body, StringComparison.Ordinal);
        Assert.All(items, i => Assert.True(i.Body.Trim().Length > 40, $"{i.Path}:{i.Line} came back with no usable body"));
    }

    [Fact]
    public void The_overviews_own_bold_lines_are_not_mistaken_for_findings()
    {
        // The body opens with "**Changes:**" and a file-summary table long
        // before the suppressed section; a parser that scans the whole body for
        // bold path-like lines reports findings that do not exist.
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(ReviewDetailsShape)));

        Assert.DoesNotContain(items, i => i.Path!.Contains("Changes", StringComparison.Ordinal));
        Assert.All(items, i => Assert.Contains('/', i.Path!));
    }

    [Fact]
    public void The_fenced_excerpt_under_a_finding_is_dropped_rather_than_read_as_prose()
    {
        // Each finding in this shape is followed by a fenced quote of the code
        // it is about. Kept, it doubles the batch and reads as a second finding.
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(ReviewDetailsShape)));

        Assert.All(items, i => Assert.DoesNotContain("```", i.Body, StringComparison.Ordinal));
        Assert.DoesNotContain(items, i => i.Body.Contains("const remove = (attachment: WorkItemAttachment)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_suppressed_finding_is_attributed_to_the_review_it_came_from()
    {
        var review = Review(Fixture(ReviewDetailsShape), id: "5250768235", head: "7e932b3de2ddb0648519528fdffbd6fdd11d1b16");

        var item = PrReviewBodyParser.Suppressed(review)[0];

        Assert.Equal(review.Id, item.ReviewId);
        Assert.Equal(review.HeadSha, item.Commit);
        Assert.Equal(review.Author, item.Author);
    }

    [Fact]
    public void The_other_shape_the_same_reviewer_emits_is_read_too()
    {
        // PR #153: the finding sits under <details><summary>Suppressed
        // comments (1)</summary> with no Review details block and no excerpt.
        var items = PrReviewBodyParser.Suppressed(Review(Fixture(DetailsSummaryShape), id: "5222030374"));

        var item = Assert.Single(items);
        Assert.Equal("frontend/src/pages/LoopEditor/utils/toolSelection.ts", item.Path);
        Assert.Equal(1, item.Line);
        Assert.Contains("optional chaining", item.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_with_no_suppressed_section_yields_none()
    {
        Assert.Empty(PrReviewBodyParser.Suppressed(Review(Fixture(NoSuppressedSection), id: "5255961714")));
        Assert.Empty(PrReviewBodyParser.Suppressed(Review("Looks good to me.")));
        Assert.Empty(PrReviewBodyParser.Suppressed(Review(string.Empty)));
        Assert.Empty(PrReviewBodyParser.Suppressed(Review(null)));
    }

    [Fact]
    public void A_malformed_section_yields_what_it_can_and_never_throws()
    {
        var body = "<details>\n<summary>Suppressed comments (3)</summary>\n\n"
            + "**src/Good.cs:12**\n* a real finding\n"
            + "**not a location at all**\n* orphaned prose\n"
            + "**src/NoLine.cs**\n* no line number\n";

        var items = PrReviewBodyParser.Suppressed(Review(body));

        Assert.Contains(items, i => i.Path == "src/Good.cs" && i.Line == 12);
        Assert.DoesNotContain(items, i => i.Path!.Contains("not a location", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unterminated_section_does_not_throw()
    {
        var body = "<details>\n<summary>Suppressed comments (2)</summary>\n\n**src/A.cs:1**\n* one";

        var items = PrReviewBodyParser.Suppressed(Review(body));

        Assert.Contains(items, i => i.Path == "src/A.cs" && i.Line == 1);
    }

    [Fact]
    public void Windows_line_endings_parse_the_same_as_unix_ones()
    {
        // GitHub hands review bodies back with CRLF; the fixtures are normalized
        // by the repository's own .gitattributes, so this is the only place the
        // real wire form is exercised.
        var body = Fixture(ReviewDetailsShape).Replace("\n", "\r\n");

        Assert.Equal(8, PrReviewBodyParser.Suppressed(Review(body)).Count);
    }

    [Fact]
    public void A_review_killed_by_its_own_spend_guard_is_flagged_incomplete()
    {
        // The load-bearing signal is the NOTE the reviewer opens with; a
        // truncated review carries no "Files reviewed" trailer to compare.
        Assert.True(PrReviewBodyParser.IsIncomplete(Fixture(DetailsSummaryShape)));
    }

    [Fact]
    public void A_review_that_ran_to_completion_is_not_flagged()
    {
        Assert.False(PrReviewBodyParser.IsIncomplete(Fixture(ReviewDetailsShape)));
        Assert.False(PrReviewBodyParser.IsIncomplete(Fixture(NoSuppressedSection)));
        Assert.False(PrReviewBodyParser.IsIncomplete(null));
        Assert.False(PrReviewBodyParser.IsIncomplete(string.Empty));
    }

    [Fact]
    public void A_review_that_reached_only_some_of_the_changed_files_is_flagged()
    {
        Assert.True(PrReviewBodyParser.IsIncomplete("- **Files reviewed:** 9/25 changed files"));
        Assert.False(PrReviewBodyParser.IsIncomplete("- **Files reviewed:** 25/25 changed files"));
    }
}
