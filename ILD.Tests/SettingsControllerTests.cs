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
            _prPoller.Object, _policy.Object, _networkNotifier.Object,
            ILD.Core.Services.Attachments.AttachmentLimits.FromEnvironment());

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
    [InlineData("-1")]
    [InlineData("3651")]
    [InlineData("thirty")]
    [InlineData("7.5")]
    public async Task A_network_log_retention_outside_the_allowed_days_is_refused_and_stores_nothing(string sent)
    {
        var result = await Put(AppSettingKeys.NetworkLogRetentionDays, sent);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await Stored(AppSettingKeys.NetworkLogRetentionDays));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3650")]
    public async Task A_network_log_retention_at_either_end_of_the_range_is_stored(string sent)
    {
        var result = await Put(AppSettingKeys.NetworkLogRetentionDays, sent);

        Assert.Equal(sent, StatedValue(result));
        Assert.Equal(sent, await Stored(AppSettingKeys.NetworkLogRetentionDays));
    }

    [Theory]
    [InlineData(" TRUE ", "true")]
    [InlineData("False", "false")]
    public async Task The_smart_titles_switch_is_stored_like_the_other_booleans(string sent, string stored)
    {
        var result = await Put(AppSettingKeys.ChatSmartTitles, sent);

        Assert.Equal(stored, StatedValue(result));
        Assert.Equal(stored, await Stored(AppSettingKeys.ChatSmartTitles));
    }

    [Fact]
    public async Task A_smart_titles_switch_no_bool_can_be_read_from_is_refused_and_stores_nothing()
    {
        var result = await Put(AppSettingKeys.ChatSmartTitles, "on");

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await Stored(AppSettingKeys.ChatSmartTitles));
    }

    [Theory]
    [InlineData(" Fast ", "Fast")]
    [InlineData("   ", "")]
    public async Task The_title_provider_tag_is_stored_trimmed(string sent, string stored)
    {
        var result = await Put(AppSettingKeys.ChatTitleProviderTag, sent);

        Assert.Equal(stored, StatedValue(result));
        Assert.Equal(stored, await Stored(AppSettingKeys.ChatTitleProviderTag));
    }

    public static TheoryData<string> ImpossibleTags => new() { new string('t', 65), "qa,fast" };

    [Theory]
    [MemberData(nameof(ImpossibleTags))]
    public async Task A_title_provider_tag_no_provider_could_hold_is_refused_and_stores_nothing(string sent)
    {
        var result = await Put(AppSettingKeys.ChatTitleProviderTag, sent);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await Stored(AppSettingKeys.ChatTitleProviderTag));
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

    [Theory]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "1")]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "5")]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "10")]
    [InlineData(AppSettingKeys.RunRetentionDays, "0")]
    [InlineData(AppSettingKeys.RunRetentionDays, "30")]
    [InlineData(AppSettingKeys.RunRetentionDays, "3650")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "1")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "500")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "8192")]
    public async Task A_value_inside_the_range_of_a_ranged_setting_is_stored(string key, string sent)
    {
        var result = await Put(key, sent);

        Assert.Equal(sent, StatedValue(result));
        Assert.Equal(sent, await Stored(key));
    }

    [Theory]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "0")]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "11")]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "-1")]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "three")]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "")]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "2.5")]
    [InlineData(AppSettingKeys.RunRetentionDays, "-1")]
    [InlineData(AppSettingKeys.RunRetentionDays, "3651")]
    [InlineData(AppSettingKeys.RunRetentionDays, "7.5")]
    [InlineData(AppSettingKeys.RunRetentionDays, "")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "0")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "8193")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "-1")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "four thousand")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "2.5")]
    public async Task A_value_outside_the_range_of_a_ranged_setting_is_refused_and_stores_nothing(string key, string sent)
    {
        var result = await Put(key, sent);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await Stored(key));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3651")]
    [InlineData("7.5")]
    [InlineData("")]
    public async Task The_run_retention_refusal_names_the_upper_limit_and_no_longer_calls_zero_when_done(string sent)
    {
        var result = await Put(AppSettingKeys.RunRetentionDays, sent);

        var error = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Contains("3650", error);
        Assert.DoesNotContain("when done", error);
        Assert.DoesNotContain("disabled", error);
    }

    [Theory]
    [InlineData(AppSettingKeys.ChatTitleMaxAttempts, "3")]
    [InlineData(AppSettingKeys.RunRetentionDays, "14")]
    [InlineData(AppSettingKeys.GlobalVariableMaxLength, "4000")]
    public async Task A_ranged_setting_reads_its_default_until_one_is_stored(string key, string expected)
    {
        Assert.Equal(expected, StatedValue(await Build().Get(key, TestContext.Current.CancellationToken)));

        var all = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Build().GetAll(TestContext.Current.CancellationToken)).Value);
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(all)!;
        Assert.Equal(expected, rows.Single(r => r["key"] == key)["value"]);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("monitor")]
    [InlineData("whitelist")]
    [InlineData("blacklist")]
    [InlineData(" Monitor ")]
    [InlineData("OFF")]
    public async Task The_network_mode_accepts_each_of_its_four_values_in_any_case_and_applies_it_to_the_next_connection(string sent)
    {
        var result = await Put(AppSettingKeys.NetworkMode, sent);

        Assert.Equal(sent, StatedValue(result));
        Assert.Equal(sent, await Stored(AppSettingKeys.NetworkMode));
        _policy.Verify(p => p.Invalidate(), Times.Once);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("")]
    [InlineData("on")]
    public async Task A_network_mode_outside_the_four_is_refused_naming_all_four_and_stores_nothing(string sent)
    {
        var result = await Put(AppSettingKeys.NetworkMode, sent);

        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(result).Value);
        var error = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!["error"];
        foreach (var mode in new[] { "off", "monitor", "whitelist", "blacklist" })
            Assert.Contains($"'{mode}'", error);
        Assert.Null(await Stored(AppSettingKeys.NetworkMode));
    }

    [Fact]
    public async Task With_nothing_stored_the_network_mode_reads_monitor_and_the_migration_record_is_hidden_and_read_only()
    {
        Assert.Equal("monitor", StatedValue(await Build().Get(AppSettingKeys.NetworkMode, TestContext.Current.CancellationToken)));

        await _db.Settings.UpsertAsync(ILD.Data.Migrations.NetworkModeMonitorMigrator.MarkerKey, "done", TestContext.Current.CancellationToken);
        var all = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Build().GetAll(TestContext.Current.CancellationToken)).Value);
        var rows = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(all)!;
        Assert.Equal("monitor", rows.Single(r => r["key"] == AppSettingKeys.NetworkMode)["value"]);
        Assert.DoesNotContain(rows, r => r["key"] == ILD.Data.Migrations.NetworkModeMonitorMigrator.MarkerKey);

        Assert.IsType<NotFoundObjectResult>(await Put(ILD.Data.Migrations.NetworkModeMonitorMigrator.MarkerKey, "x"));
        Assert.Equal("done", await Stored(ILD.Data.Migrations.NetworkModeMonitorMigrator.MarkerKey));
    }

    [Fact]
    public async Task The_retired_internal_allow_key_is_an_unknown_setting()
    {
        Assert.IsType<NotFoundResult>(await Build().Get("network.internalAllow", TestContext.Current.CancellationToken));
        Assert.IsType<NotFoundObjectResult>(await Put("network.internalAllow", "10.0.0.0/8"));
        Assert.Null(await Stored("network.internalAllow"));

        var all = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(await Build().GetAll(TestContext.Current.CancellationToken)).Value);
        Assert.DoesNotContain("network.internalAllow", all);
    }
}
