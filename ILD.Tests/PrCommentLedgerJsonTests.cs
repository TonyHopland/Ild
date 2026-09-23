using ILD.Data.DTOs;

namespace ILD.Tests;

/// <summary>
/// The persisted form of what a run has been handed on its PR's review. It is
/// read on every heartbeat tick of every parked run, so a blob it cannot read
/// must degrade rather than throw — and it must degrade in the safe direction,
/// since "no ledger" means a first watch, which records everything and fires
/// nothing, while a half-read one would hand the agent the PR's whole history.
/// </summary>
public class PrCommentLedgerJsonTests
{
    private const string Head = "7e932b3de2ddb0648519528fdffbd6fdd11d1b16";

    [Fact]
    public void A_ledger_survives_a_round_trip_intact()
    {
        var ledger = new PrCommentLedger(
            Head, new[] { "issue:1" }, new[] { "review:2", "review:3" }, new[] { "abc123" });

        var back = PrCommentLedgerJson.TryParse(PrCommentLedgerJson.Serialize(ledger));

        Assert.NotNull(back);
        Assert.Equal(ledger.Head, back!.Head);
        Assert.Equal(ledger.PostedIds, back.PostedIds);
        Assert.Equal(ledger.DeliveredIds, back.DeliveredIds);
        Assert.Equal(ledger.DeliveredHashes, back.DeliveredHashes);
    }

    [Fact]
    public void A_blob_that_cannot_be_read_is_no_ledger_rather_than_an_exception()
    {
        Assert.Null(PrCommentLedgerJson.TryParse(null));
        Assert.Null(PrCommentLedgerJson.TryParse(string.Empty));
        Assert.Null(PrCommentLedgerJson.TryParse("{not json"));
        Assert.Null(PrCommentLedgerJson.TryParse("[]"));
    }

    [Fact]
    public void A_ledger_written_by_a_newer_version_is_not_half_read()
    {
        // Reading a shape this version does not know would silently drop
        // whatever it did not recognise — including the posted ids that stop the
        // loop answering itself. Not reading it at all costs one quiet tick.
        Assert.Null(PrCommentLedgerJson.TryParse("{\"v\":2,\"head\":\"" + Head + "\",\"postedIds\":[\"issue:1\"]}"));
    }

    [Fact]
    public void A_ledger_missing_its_lists_reads_as_empty_ones()
    {
        var ledger = PrCommentLedgerJson.TryParse("{\"v\":1,\"head\":\"" + Head + "\"}");

        Assert.NotNull(ledger);
        Assert.Equal(Head, ledger!.Head);
        Assert.Empty(ledger.PostedIds);
        Assert.Empty(ledger.DeliveredIds);
        Assert.Empty(ledger.DeliveredHashes);
    }

    [Fact]
    public void A_long_lived_run_forgets_the_oldest_rather_than_growing_the_column_without_bound()
    {
        var existing = Enumerable.Range(0, PrCommentLedger.MaxRemembered).Select(i => $"review:{i}").ToArray();

        var kept = PrCommentLedger.Remember(existing, "review:newest");

        Assert.Equal(PrCommentLedger.MaxRemembered, kept.Count);
        Assert.Equal("review:newest", kept[0]);
        Assert.DoesNotContain($"review:{PrCommentLedger.MaxRemembered - 1}", kept);
    }

    [Fact]
    public void Recording_the_same_posted_comment_twice_keeps_one_entry()
    {
        var ledger = PrCommentLedger.Empty.WithPosted("issue:1").WithPosted("issue:1");

        Assert.Equal(new[] { "issue:1" }, ledger.PostedIds);
    }

    [Fact]
    public void Two_id_spaces_that_can_collide_are_kept_apart()
    {
        // An inline comment and a pull-request-level comment can carry the same
        // number; namespacing is what stops one suppressing the other.
        Assert.NotEqual(PrCommentLedger.KeyFor("issue", "4053396920"), PrCommentLedger.KeyFor("review", "4053396920"));
    }

    [Fact]
    public void The_same_finding_reflowed_fingerprints_the_same_and_a_different_place_does_not()
    {
        var original = PrCommentLedger.Fingerprint("src/A.cs", 10, "new byte[file.Length] will not compile");

        Assert.Equal(original, PrCommentLedger.Fingerprint("src/A.cs", 10, "  new byte[file.Length]\n  will not compile  "));
        Assert.NotEqual(original, PrCommentLedger.Fingerprint("src/B.cs", 10, "new byte[file.Length] will not compile"));
        Assert.NotEqual(original, PrCommentLedger.Fingerprint("src/A.cs", 11, "new byte[file.Length] will not compile"));
    }
}
