using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class RunPreparationsTests
{
    [Fact]
    public void A_work_item_is_preparing_from_Begin_until_End()
    {
        var preparations = new RunPreparations();
        Assert.False(preparations.IsPreparing("WI-1"));

        preparations.Begin("WI-1");
        Assert.True(preparations.IsPreparing("WI-1"));
        Assert.False(preparations.IsPreparing("WI-2"));

        preparations.End("WI-1");
        Assert.False(preparations.IsPreparing("WI-1"));
    }

    [Fact]
    public void Ending_a_work_item_that_was_not_preparing_does_nothing()
    {
        var preparations = new RunPreparations();
        preparations.End("WI-1");
        Assert.False(preparations.IsPreparing("WI-1"));
    }
}
