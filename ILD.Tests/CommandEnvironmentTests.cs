using ILD.Core.Services.RunHosts;

namespace ILD.Tests;

/// <summary>The one rule both run hosts merge a command's environment by: run defaults, then the caller's, then the host's own keys.</summary>
public sealed class CommandEnvironmentTests
{
    private static readonly char Sep = Path.PathSeparator;

    private static Dictionary<string, string?> Vars(params (string Name, string? Value)[] variables)
        => variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public void The_caller_wins_over_the_defaults_and_the_host_wins_over_the_caller()
    {
        var merged = CommandEnvironment.Merge(
            Vars(("A", "default"), ("B", "default"), ("HOME", "/default")),
            Vars(("B", "caller"), ("HOME", "/caller"), ("C", "caller")),
            Vars(("HOME", "/host")));

        Assert.Equal("default", merged.Variables["A"]);
        Assert.Equal("caller", merged.Variables["B"]);
        Assert.Equal("caller", merged.Variables["C"]);
        Assert.Equal("/host", merged.Variables["HOME"]);
    }

    [Fact]
    public void A_null_unsets_for_the_caller_and_drops_for_the_host()
    {
        var merged = CommandEnvironment.Merge(
            Vars(("TOKEN", "inherited"), ("NPM_CONFIG_PREFIX", "/default")),
            Vars(("TOKEN", null), ("NPM_CONFIG_PREFIX", "/caller")),
            Vars(("NPM_CONFIG_PREFIX", null)));

        Assert.DoesNotContain("TOKEN", merged.Variables.Keys);
        Assert.DoesNotContain("NPM_CONFIG_PREFIX", merged.Variables.Keys);
    }

    [Fact]
    public void A_callers_path_goes_in_front_of_a_known_run_path_and_never_replaces_it()
    {
        var run = string.Join(Sep, "/run/bin", "/usr/bin");

        var merged = CommandEnvironment.Merge(Vars(("PATH", run)), Vars(("PATH", string.Join(Sep, "/caller/bin", "/usr/bin"))), null);

        Assert.Equal(string.Join(Sep, "/caller/bin", "/run/bin", "/usr/bin"), merged.Variables["PATH"]);
        Assert.Equal("", merged.PathPrefix);
    }

    [Fact]
    public void A_callers_path_is_a_prefix_where_the_runs_path_is_not_known()
    {
        var merged = CommandEnvironment.Merge(null, Vars(("PATH", "/caller/bin")), null);

        Assert.DoesNotContain("PATH", merged.Variables.Keys);
        Assert.Equal("/caller/bin", merged.PathPrefix);
    }

    [Fact]
    public void A_command_with_no_path_of_its_own_leaves_the_runs_path_as_it_is()
    {
        var merged = CommandEnvironment.Merge(Vars(("PATH", "/run/bin")), Vars(("A", "1")), null);

        Assert.Equal("/run/bin", merged.Variables["PATH"]);
        Assert.Equal("", merged.PathPrefix);
    }

    [Fact]
    public void The_exports_quote_every_value_and_refuse_a_name_a_shell_cannot_set()
    {
        var text = CommandEnvironment.ShellExports(new Dictionary<string, string> { ["A"] = "it's $HOME\nnext", ["_b1"] = "" });

        Assert.Equal("export A='it'\\''s $HOME\nnext'\nexport _b1=''\n", text);
        Assert.Throws<InvalidOperationException>(() => CommandEnvironment.ShellExports(new Dictionary<string, string> { ["A B"] = "x" }));
        Assert.Throws<InvalidOperationException>(() => CommandEnvironment.ShellExports(new Dictionary<string, string> { ["A=B"] = "x" }));
    }
}
