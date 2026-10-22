using Npgsql;

namespace ILD.Tests;

/// <summary>
/// The pool caps each service puts on the operator's connection string. ILD.Api and
/// ILD.WorkItemServer carry their own copy of the helper (the server does not reference
/// ILD.Data), so every case runs against both.
/// </summary>
public class NpgsqlPoolDefaultsTests
{
    private static readonly string[] CopyNames = ["ILD.Data", "ILD.WorkItemServer"];

    public static TheoryData<string> Copies => new(CopyNames);

    private static string Apply(string copy, string connectionString, string applicationName, int maxPoolSize) => copy switch
    {
        "ILD.Data" => ILD.Data.NpgsqlPoolDefaults.Apply(connectionString, applicationName, maxPoolSize),
        "ILD.WorkItemServer" => ILD.WorkItemServer.NpgsqlPoolDefaults.Apply(connectionString, applicationName, maxPoolSize),
        _ => throw new ArgumentOutOfRangeException(nameof(copy)),
    };

    [Theory]
    [MemberData(nameof(Copies))]
    public void The_caps_are_added_and_every_other_key_is_kept(string copy)
    {
        const string operatorString =
            "Host=db.example.test;Port=6543;Database=ild_core;Username=ild;Password='hun;ter2';" +
            "SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem;Include Error Detail=true";

        var result = new NpgsqlConnectionStringBuilder(Apply(copy, operatorString, "ILD.Api", 15));

        Assert.Equal(15, result.MaxPoolSize);
        Assert.Equal(60, result.ConnectionIdleLifetime);
        Assert.Equal("ILD.Api", result.ApplicationName);
        var original = new NpgsqlConnectionStringBuilder(operatorString);
        Assert.Equal(original.Host, result.Host);
        Assert.Equal(original.Port, result.Port);
        Assert.Equal(original.Database, result.Database);
        Assert.Equal(original.Username, result.Username);
        Assert.Equal("hun;ter2", result.Password);
        Assert.Equal(original.SslMode, result.SslMode);
        Assert.Equal(original.RootCertificate, result.RootCertificate);
        Assert.Equal(original.IncludeErrorDetail, result.IncludeErrorDetail);
    }

    [Theory]
    [MemberData(nameof(Copies))]
    public void No_timeout_is_added_so_a_full_pool_makes_a_request_wait(string copy)
    {
        var result = Apply(copy, "Host=db.example.test;Database=ild", "ILD.WorkItemServer", 8);

        Assert.DoesNotContain("Timeout", result, StringComparison.OrdinalIgnoreCase);
    }

    // Values equal to Npgsql's own defaults (100, 300) are included on purpose: the
    // operator wrote them, so they stand.
    [Theory]
    [InlineData("MaxPoolSize=5", 5, 60, "ILD.Api")]
    [InlineData("Maximum Pool Size=100", 100, 60, "ILD.Api")]
    [InlineData("ApplicationName=x", 15, 60, "x")]
    [InlineData("Application Name=preview-api", 15, 60, "preview-api")]
    [InlineData("Connection Idle Lifetime=300", 15, 300, "ILD.Api")]
    [InlineData("ConnectionIdleLifetime=10;MaxPoolSize=3;ApplicationName=mine", 3, 10, "mine")]
    public void A_value_the_operator_set_wins_whatever_alias_it_is_written_with(
        string explicitKeys, int maxPoolSize, int idleLifetime, string applicationName)
    {
        foreach (var copy in CopyNames)
        {
            var result = new NpgsqlConnectionStringBuilder(
                Apply(copy, "Host=db.example.test;Database=ild;" + explicitKeys, "ILD.Api", 15));

            Assert.Equal(maxPoolSize, result.MaxPoolSize);
            Assert.Equal(idleLifetime, result.ConnectionIdleLifetime);
            Assert.Equal(applicationName, result.ApplicationName);
        }
    }
    [Theory]
    [MemberData(nameof(Copies))]
    public void A_default_never_undercuts_a_minimum_or_pruning_interval_the_operator_set(string copy)
    {
        var result = Apply(copy, "Host=db.example.test;Database=ild;Minimum Pool Size=20;Connection Pruning Interval=120", "ILD.Api", 15);

        using var dataSource = NpgsqlDataSource.Create(result);
        var built = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(20, built.MaxPoolSize);
        Assert.Equal(120, built.ConnectionIdleLifetime);
    }
}
