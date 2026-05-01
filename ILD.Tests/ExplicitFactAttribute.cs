namespace ILD.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless <c>RUN_EXPLICIT_TESTS</c> is set to <c>true</c>.
/// Use for tests that require external dependencies (real LLM, network, etc.).
/// </summary>
public class ExplicitFactAttribute : FactAttribute
{
    public ExplicitFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_EXPLICIT_TESTS") != "true")
        {
            Skip = "Test only runs when RUN_EXPLICIT_TESTS is set to true.";
        }
    }
}