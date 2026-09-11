// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ErpImportCronTimeZone.ResolveAsync: an explicitly configured ERP_IMPORT_CRON_TIMEZONE
/// setting wins, otherwise the company's own configured time zone is used - never a hard-coded regional
/// default such as Europe/Zurich.
/// </summary>

using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpImportCronTimeZoneTests
{
    [Test]
    public async Task ResolveAsync_ExplicitSettingConfigured_UsesIt()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Vienna" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance);

        result.ShouldBe("Europe/Vienna");
    }

    [Test]
    public async Task ResolveAsync_NoSettingConfigured_FallsBackToTheCompanyZone()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId).Returns((SettingsModel?)null);
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance);

        result.ShouldBe("Asia/Kolkata");
    }

    [Test]
    public async Task ResolveAsync_ExplicitWindowsSettingConfigured_ReturnsTheIanaId()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "W. Europe Standard Time" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance);

        result.ShouldBe("Europe/Berlin");
    }

    [Test]
    public async Task ResolveAsync_NoSettingConfigured_WindowsCompanyZone_ReturnsTheIanaId()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId).Returns((SettingsModel?)null);
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance);

        result.ShouldBe("Europe/Berlin");
    }

    [Test]
    public async Task ResolveAsync_UnresolvableSettingConfigured_FallsBackToTheCompanyZone()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Not/AZone" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance);

        result.ShouldBe("Asia/Kolkata");
    }

    [Test]
    public async Task ResolveAsync_UnresolvableSettingConfigured_LogsWarning()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Not/AZone" });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var logger = Substitute.For<ILogger>();

        await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, logger);

        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ResolveAsync_BlankSettingConfigured_FallsBackToTheCompanyZone()
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "  " });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

        var result = await ErpImportCronTimeZone.ResolveAsync(settingsReader, companyClock, NullLogger.Instance);

        result.ShouldBe("UTC");
    }
}
