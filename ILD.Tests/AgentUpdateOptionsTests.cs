using ILD.Core.Services.Implementations;
using Microsoft.Extensions.Logging;

namespace ILD.Tests;

public class AgentUpdateOptionsTests
{
    private static int CooldownFor(string? value)
        => AgentUpdateOptions.FromEnvironment(readVariable: name =>
            name == AgentUpdateOptions.CooldownDaysVariable ? value : null).CooldownDays;

    [Fact]
    public void The_default_is_one_day_when_the_variable_is_missing_or_blank()
    {
        Assert.Equal(1, CooldownFor(null));
        Assert.Equal(1, CooldownFor(""));
        Assert.Equal(1, CooldownFor("  "));
        Assert.Equal(1, new AgentUpdateOptions().CooldownDays);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("365", 365)]
    public void A_whole_number_from_0_to_365_is_used(string value, int expected)
        => Assert.Equal(expected, CooldownFor(value));

    private sealed class Sink : ILogger
    {
        public List<LogLevel> Levels { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Levels.Add(logLevel);
    }

    [Fact]
    public void An_invalid_value_is_warned_about_and_a_valid_or_missing_one_is_not()
    {
        var sink = new Sink();
        AgentUpdateOptions.FromEnvironment(sink, _ => "soon");
        Assert.Equal([LogLevel.Warning], sink.Levels);

        sink.Levels.Clear();
        AgentUpdateOptions.FromEnvironment(sink, _ => "3");
        AgentUpdateOptions.FromEnvironment(sink, _ => null);
        Assert.Empty(sink.Levels);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("366")]
    [InlineData("7.5")]
    [InlineData("a week")]
    [InlineData("99999999999")]
    public void Anything_else_falls_back_to_one_day(string value)
        => Assert.Equal(1, CooldownFor(value));
}
