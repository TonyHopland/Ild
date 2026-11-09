using ILD.Core.Services.RunHosts.Isolated;

namespace ILD.Tests;

public sealed class ForwardAddressesTests
{
    [Fact]
    public void A_host_gets_the_same_address_whatever_its_case_or_the_hosts_beside_it()
    {
        var alone = ForwardAddresses.Allocate(["postgres"])["postgres"];

        Assert.Equal(alone, ForwardAddresses.Allocate(["Postgres"])["Postgres"]);
        Assert.Equal(alone, ForwardAddresses.Allocate(["postgres", "postgres"])["postgres"]);
        Assert.Matches(@"^127\.1\.\d+\.\d+$", alone);
    }

    [Fact]
    public void An_address_is_never_a_networks_first_or_broadcast_address()
    {
        var addresses = ForwardAddresses.Allocate(Enumerable.Range(0, 3000).Select(i => $"host{i}.example"));

        Assert.Equal(3000, addresses.Values.Distinct().Count());
        Assert.All(addresses.Values, a => Assert.DoesNotMatch(@"\.(0|255)$", a));
    }

    [Fact]
    public void Hosts_that_hash_to_one_address_are_given_different_ones_the_same_way_every_time()
    {
        // Birthday search for two names in one slot, so the clash is real and not assumed.
        var seen = new Dictionary<string, string>();
        string a = "", b = "";
        for (var i = 0; ; i++)
        {
            var host = $"h{i}";
            var address = ForwardAddresses.Allocate([host])[host];
            if (seen.TryGetValue(address, out var other)) { a = other; b = host; break; }
            seen[address] = host;
        }

        var both = ForwardAddresses.Allocate([b, a]);
        Assert.NotEqual(both[a], both[b]);
        Assert.Equal(both, ForwardAddresses.Allocate([a, b]));
    }

    [Fact]
    public void An_ip_address_has_no_name_to_map_and_gets_none()
    {
        var addresses = ForwardAddresses.Allocate(["10.0.0.5", "::1", "postgres"]);

        Assert.Equal(["postgres"], addresses.Keys);
    }
}
