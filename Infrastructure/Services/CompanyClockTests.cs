// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Infrastructure.Services;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;

[TestFixture]
public class CompanyClockTests
{
    private ISettingsReader _settingsReader = null!;
    private ISettingsChangeVersion _settingsChangeVersion = null!;
    private Dictionary<string, string> _settings = null!;
    private long _version;

    [SetUp]
    public void SetUp()
    {
        _settings = new Dictionary<string, string>(StringComparer.Ordinal);
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>()).Returns(info =>
        {
            var requestedTypes = info.Arg<IEnumerable<string>>();
            return (IReadOnlyDictionary<string, string>)requestedTypes
                .Where(_settings.ContainsKey)
                .ToDictionary(type => type, type => _settings[type], StringComparer.Ordinal);
        });
        _version = 1;
        _settingsChangeVersion = Substitute.For<ISettingsChangeVersion>();
        _settingsChangeVersion.Current.Returns(_ => _version);
        _settingsChangeVersion.When(x => x.Bump()).Do(_ => _version++);
    }

    [Test]
    public async Task GetTodayAsync_ExplicitTimeZoneAheadOfUtc_ReturnsLocalDateAsUtcMidnight()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Tokyo");
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        today.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task GetTodayAsync_NoTimeZoneButCountryConfigured_FallsBackToCountryZone()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_COUNTRY, "CH");
        var clock = CreateClock("2026-06-27T22:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GetTodayAsync_NoAddressButGlobalCalendarCountryConfigured_FallsBackToCalendarCountryZone()
    {
        SetSetting(SettingKeys.GlobalCalendarCountry, "JP");
        var clock = CreateClock("2026-06-27T16:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GetTimeZoneAsync_AddressCountryAndGlobalCalendarCountryBothConfigured_AddressCountryWins()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_COUNTRY, "CH");
        SetSetting(SettingKeys.GlobalCalendarCountry, "JP");
        var clock = CreateClock("2026-06-27T16:30:00Z");

        var zone = await clock.GetTimeZoneAsync();

        zone.Id.ShouldBe("Europe/Zurich");
    }

    [Test]
    public async Task GetTodayAsync_NoTimeZoneAndNoCountry_FallsBackToUtc()
    {
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 27, 0, 0, 0, DateTimeKind.Utc));
        today.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task GetTodayAsync_InvalidTimeZoneId_FallsThroughToCountryZone()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Mars/Phobos");
        SetSetting(SettingsConstants.APP_ADDRESS_COUNTRY, "DE");
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var today = await clock.GetTodayAsync();

        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GetTimeZoneAsync_KolkataConfigured_UtcOffsetIsPlus0530()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Kolkata");
        var clock = CreateClock("2026-06-27T12:00:00Z");

        var zone = await clock.GetTimeZoneAsync();

        zone.GetUtcOffset(DateTimeOffset.Parse("2026-06-27T12:00:00Z")).ShouldBe(TimeSpan.FromHours(5.5));
    }

    [Test]
    public async Task GetTimeZoneAsync_KolkataConfigured_IdEchoesTheConfiguredIanaId()
    {
        // .NET on Windows resolves IANA ids via ICU since .NET 6; verified empirically on this dev box
        // (.NET 10.0.6 / Windows 11): TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata").Id returns
        // "Asia/Kolkata" unchanged, not a Windows display name. If this assertion ever starts failing on
        // a machine without full ICU data, drop it in favour of the offset-only assertion above - the
        // offset is the behaviourally load-bearing part, this Id check is a bonus guard.
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Kolkata");
        var clock = CreateClock("2026-06-27T12:00:00Z");

        var zone = await clock.GetTimeZoneAsync();

        zone.Id.ShouldBe("Asia/Kolkata");
    }

    [Test]
    public async Task GetTimeZoneAsync_StJohnsConfigured_JulyUtcOffsetIsMinus0230DuringDst()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "America/St_Johns");
        var clock = CreateClock("2026-07-15T12:00:00Z");

        var zone = await clock.GetTimeZoneAsync();

        zone.GetUtcOffset(DateTimeOffset.Parse("2026-07-15T12:00:00Z")).ShouldBe(TimeSpan.FromHours(-2.5));
    }

    [Test]
    public async Task GetNowAsync_AndGetTodayDateAsync_AgreeWithGetTodayAsync()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Tokyo");
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var now = await clock.GetNowAsync();
        var todayDate = await clock.GetTodayDateAsync();
        var today = await clock.GetTodayAsync();

        now.Offset.ShouldBe(TimeSpan.FromHours(9));
        DateOnly.FromDateTime(now.Date).ShouldBe(todayDate);
        todayDate.ShouldBe(new DateOnly(2026, 6, 28));
        today.ShouldBe(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task GetTimeZoneAsync_ResultIsMemoised_SettingsReaderReadOnlyOnceAcrossMultipleCalls()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Tokyo");
        var clock = CreateClock("2026-06-27T23:30:00Z");

        await clock.GetTimeZoneAsync();
        await clock.GetTimeZoneAsync();
        await clock.GetNowAsync();

        await _settingsReader.Received(1).GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>());
    }

    [Test]
    public async Task GetTimeZoneAsync_AfterSettingsChangeVersionBumps_ReResolvesTheZone()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Tokyo");
        var clock = CreateClock("2026-06-27T23:30:00Z");
        await clock.GetTimeZoneAsync();

        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "America/St_Johns");
        _settingsChangeVersion.Bump();

        var zone = await clock.GetTimeZoneAsync();

        zone.Id.ShouldBe("America/St_Johns");
        await _settingsReader.Received(2).GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>());
    }

    [Test]
    public async Task GetTimeZoneResolutionAsync_ExplicitTimeZoneConfigured_SourceIsSetting()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Kolkata");
        var clock = CreateClock("2026-06-27T12:00:00Z");

        var resolution = await clock.GetTimeZoneResolutionAsync();

        resolution.Zone.Id.ShouldBe("Asia/Kolkata");
        resolution.Source.ShouldBe(CompanyTimeZoneSource.Setting);
    }

    [Test]
    public async Task GetTimeZoneResolutionAsync_OnlyAddressCountryConfigured_SourceIsAddressCountry()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_COUNTRY, "CH");
        var clock = CreateClock("2026-06-27T12:00:00Z");

        var resolution = await clock.GetTimeZoneResolutionAsync();

        resolution.Zone.Id.ShouldBe("Europe/Zurich");
        resolution.Source.ShouldBe(CompanyTimeZoneSource.AddressCountry);
    }

    [Test]
    public async Task GetTimeZoneResolutionAsync_OnlyGlobalCalendarCountryConfigured_SourceIsCalendarCountry()
    {
        SetSetting(SettingKeys.GlobalCalendarCountry, "JP");
        var clock = CreateClock("2026-06-27T12:00:00Z");

        var resolution = await clock.GetTimeZoneResolutionAsync();

        resolution.Zone.Id.ShouldBe("Asia/Tokyo");
        resolution.Source.ShouldBe(CompanyTimeZoneSource.CalendarCountry);
    }

    [Test]
    public async Task GetTimeZoneResolutionAsync_NothingConfigured_SourceIsUtc()
    {
        var clock = CreateClock("2026-06-27T12:00:00Z");

        var resolution = await clock.GetTimeZoneResolutionAsync();

        resolution.Zone.ShouldBe(TimeZoneInfo.Utc);
        resolution.Source.ShouldBe(CompanyTimeZoneSource.Utc);
    }

    [Test]
    public async Task GetTimeZoneResolutionAsync_InvalidExplicitZone_FallsThroughToCountrySource()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Mars/Phobos");
        SetSetting(SettingsConstants.APP_ADDRESS_COUNTRY, "DE");
        var clock = CreateClock("2026-06-27T12:00:00Z");

        var resolution = await clock.GetTimeZoneResolutionAsync();

        resolution.Zone.Id.ShouldBe("Europe/Berlin");
        resolution.Source.ShouldBe(CompanyTimeZoneSource.AddressCountry);
    }

    [Test]
    public async Task GetTimeZoneAsync_AndGetTimeZoneResolutionAsync_ShareTheSameMemoAndZone()
    {
        SetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE, "Asia/Tokyo");
        var clock = CreateClock("2026-06-27T23:30:00Z");

        var zone = await clock.GetTimeZoneAsync();
        var resolution = await clock.GetTimeZoneResolutionAsync();

        zone.ShouldBe(resolution.Zone);
        await _settingsReader.Received(1).GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>());
    }

    private void SetSetting(string type, string value)
    {
        _settings[type] = value;
    }

    private CompanyClock CreateClock(string utcInstant)
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(DateTimeOffset.Parse(
            utcInstant,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal));
        return new CompanyClock(_settingsReader, timeProvider, _settingsChangeVersion);
    }
}
