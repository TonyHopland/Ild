using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class BranchNameRulesTests
{
    [Theory]
    [InlineData("feature/foo")]
    [InlineData("release/1.0")]
    [InlineData("wip")]
    [InlineData("a/b/c-d_e.f")]
    [InlineData("ild/wi-40-run-1")]
    public void Accepts_names_git_accepts(string name)
        => Assert.Null(BranchNameRules.Validate(name));

    [Theory]
    // git check-ref-format's rules, in the order a human is likely to trip them.
    [InlineData("feature foo")]        // space
    [InlineData("feature\tfoo")]       // control character
    [InlineData("feature~1")]
    [InlineData("feature^1")]
    [InlineData("feature:foo")]
    [InlineData("feature?")]
    [InlineData("feature*")]
    [InlineData("feature[1]")]
    [InlineData("feature\\foo")]       // backslash: legal to git, a path split to us
    [InlineData("feature/../escape")]
    [InlineData("feature@{1}")]
    [InlineData("@")]
    [InlineData("-feature")]
    [InlineData("/feature")]
    [InlineData("feature/")]
    [InlineData("feature//foo")]
    [InlineData("feature.")]
    [InlineData(".hidden")]
    [InlineData("feature/.hidden")]
    [InlineData("feature.lock")]
    [InlineData("feature/foo.lock")]
    public void Rejects_names_git_rejects(string name)
        => Assert.NotNull(BranchNameRules.Validate(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_name_that_is_not_there(string? name)
        => Assert.NotNull(BranchNameRules.Validate(name));

    [Fact]
    public void Rejects_a_name_longer_than_the_run_branch_column()
    {
        // A name that cannot be persisted on LoopRun.BranchName is no use even
        // if git would take it.
        Assert.Null(BranchNameRules.Validate(new string('a', BranchNameRules.MaxLength)));
        Assert.NotNull(BranchNameRules.Validate(new string('a', BranchNameRules.MaxLength + 1)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  feature/foo  ", "feature/foo")]
    public void Normalize_collapses_blank_to_no_override(string? raw, string? expected)
        => Assert.Equal(expected, BranchNameRules.Normalize(raw));
}
