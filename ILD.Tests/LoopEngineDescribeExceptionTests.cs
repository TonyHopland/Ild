using ILD.Core.Services.Implementations;

namespace ILD.Tests;

public class LoopEngineDescribeExceptionTests
{
    [Fact]
    public void Surfaces_the_inner_exception_behind_a_saveChanges_failure()
    {
        // The shape EF Core produces on a persistence failure: a generic outer
        // message wrapping the driver's real constraint/column error. The crash
        // reason on the run and in the work-item conversation must carry the
        // inner cause, not just the opaque outer line.
        var ex = new Exception(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            new Exception("23502: null value in column \"AiProvider\" violates not-null constraint"));

        var described = LoopEngine.DescribeException(ex);

        Assert.Contains("An error occurred while saving the entity changes", described);
        Assert.Contains("null value in column \"AiProvider\"", described);
        Assert.Contains(" → ", described);
    }

    [Fact]
    public void Drops_blank_and_consecutive_duplicate_messages()
    {
        var ex = new Exception("boom", new Exception("boom", new Exception("   ")));

        var described = LoopEngine.DescribeException(ex);

        Assert.Equal("boom", described);
    }

    [Fact]
    public void Falls_back_to_the_type_name_when_no_message_is_present()
    {
        var described = LoopEngine.DescribeException(new Exception(string.Empty));

        Assert.Equal(nameof(Exception), described);
    }
}
