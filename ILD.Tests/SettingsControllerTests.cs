using ILD.Api.Controllers;
using ILD.Core.Services.Implementations.Network;
using ILD.Core.Services.Interfaces;
using ILD.Core.Services.Remote;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ILD.Tests;

public sealed class SettingsControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly Mock<IWorkItemNotifier> _notifier = new();
    private readonly Mock<IWorkItemScheduler> _scheduler = new();
    private readonly Mock<ISchedulerSettingsService> _schedulerSettings = new();
    private readonly Mock<IPrStatusPoller> _prPoller = new();
    private readonly Mock<IEgressPolicy> _policy = new();
    private readonly Mock<INetworkNotifier> _networkNotifier = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Build()
        => new(_db.Settings, _notifier.Object, _scheduler.Object, _schedulerSettings.Object,
            _prPoller.Object, _policy.Object, _networkNotifier.Object);

    private Task<IActionResult> Put(string key, string value)
        => Build().Put(key, new SettingsController.UpdateSettingRequest { Value = value }, default);

    private async Task<string?> Stored(string key) => (await _db.Settings.GetByKeyAsync(key))?.Value;

    private static string StatedValue(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value!;
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!["value"];
    }

    [Theory]
    [InlineData(" true ")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public async Task A_boolean_setting_stores_a_true_however_it_was_spelled_as_the_literal_true(string spelling)
    {
        var result = await Put(AppSettingKeys.SchedulerIsPaused, spelling);

        Assert.Equal("true", StatedValue(result));
        Assert.Equal("true", await Stored(AppSettingKeys.SchedulerIsPaused));
    }

    [Theory]
    [InlineData("FALSE")]
    [InlineData(" false ")]
    public async Task A_boolean_setting_stores_a_false_however_it_was_spelled_as_the_literal_false(string spelling)
    {
        var result = await Put(AppSettingKeys.ThrottleAutoResume, spelling);

        Assert.Equal("false", StatedValue(result));
        Assert.Equal("false", await Stored(AppSettingKeys.ThrottleAutoResume));
    }

    [Fact]
    public async Task A_value_no_bool_can_be_read_from_is_still_refused_and_stores_nothing()
    {
        var result = await Put(AppSettingKeys.SchedulerIsPaused, "yes");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await Stored(AppSettingKeys.SchedulerIsPaused));
    }

    [Theory]
    [InlineData(AppSettingKeys.NetworkMode, " Whitelist ")]
    [InlineData(AppSettingKeys.SchedulerMaxConcurrent, " 7 ")]
    public async Task Canonicalising_is_for_the_booleans_only_and_leaves_every_other_value_as_sent(string key, string sent)
    {
        var result = await Put(key, sent);

        Assert.Equal(sent, StatedValue(result));
        Assert.Equal(sent, await Stored(key));
    }
}
