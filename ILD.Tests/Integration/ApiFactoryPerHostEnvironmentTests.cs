using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ILD.Tests.Integration;

/// <summary>
/// Two <see cref="ApiFactory"/> hosts that are both up at once each keep the data root they
/// were given. Both are built before either is used, so a value one of them left in the
/// process environment would reach the other.
/// </summary>
public class ApiFactoryPerHostEnvironmentTests
{
    [Fact]
    public async Task Each_host_keeps_its_data_and_worktrees_under_its_own_data_root()
    {
        await using var first = new ApiFactory();
        await using var second = new ApiFactory();
        var firstConfig = first.Services.GetRequiredService<IConfiguration>();
        var secondConfig = second.Services.GetRequiredService<IConfiguration>();

        Assert.NotEqual(first.DataRoot, second.DataRoot);
        foreach (var (host, config) in new[] { (first, firstConfig), (second, secondConfig) })
        {
            Assert.Equal(host.DataRoot, Path.GetFullPath(config["App:DataPath"]!));
            Assert.Equal(Path.Combine(host.DataRoot, "worktrees"), Path.GetFullPath(config["App:WorktreesPath"]!));
            Assert.True(Directory.Exists(Path.Combine(host.DataRoot, "worktrees")));
        }
    }
}
