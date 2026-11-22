using ILD.Data;

namespace ILD.Tests;

public class ModelIdsTests
{
    [Fact]
    public void Ids_round_trip_in_order()
        => Assert.Equal(new[] { "claude-sonnet-5-5", "claude-haiku-5-5" },
            ModelIds.Split(ModelIds.Join(new[] { "claude-sonnet-5-5", "claude-haiku-5-5" })));

    [Fact]
    public void A_single_id_is_kept_as_it_is()
    {
        Assert.Equal("claude-haiku-5-5", ModelIds.Join(new[] { "claude-haiku-5-5" }));
        Assert.Equal(new[] { "claude-haiku-5-5" }, ModelIds.Split("claude-haiku-5-5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ,")]
    public void Nothing_splits_to_no_ids(string? joined) => Assert.Empty(ModelIds.Split(joined));

    [Fact]
    public void No_ids_join_to_null()
    {
        Assert.Null(ModelIds.Join(Array.Empty<string>()));
        Assert.Null(ModelIds.Join(new[] { " ", "" }));
    }

    [Fact]
    public void Entries_are_trimmed_and_duplicates_are_left_for_the_caller()
        => Assert.Equal(new[] { "a", "b", "a" }, ModelIds.Split(" a , b,a "));
}
