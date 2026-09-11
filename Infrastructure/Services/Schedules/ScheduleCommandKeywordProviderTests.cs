// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleCommandKeywordProvider — verifies that every keyword falls back to
/// its English default when unset/blank, and reflects the configured value when set.
/// </summary>

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ScheduleCommandKeywordProviderTests
{
    private readonly Dictionary<string, string> _settingsValues = new();

    private ISettingsReader _settingsReader = null!;
    private ILogger<ScheduleCommandKeywordProvider> _logger = null!;
    private ScheduleCommandKeywordProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsValues.Clear();
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(_settingsValues));
        _logger = Substitute.For<ILogger<ScheduleCommandKeywordProvider>>();
        _provider = new ScheduleCommandKeywordProvider(_settingsReader, _logger);
    }

    [Test]
    public async Task NoSettingsConfigured_FallsBackToEnglishDefaults()
    {
        var result = await _provider.GetAsync();

        result.FreeToken.ShouldBe("FREE");
        result.NegFreeToken.ShouldBe("-FREE");
        result.EarlyToken.ShouldBe("EARLY");
        result.NegEarlyToken.ShouldBe("-EARLY");
        result.LateToken.ShouldBe("LATE");
        result.NegLateToken.ShouldBe("-LATE");
        result.NightToken.ShouldBe("NIGHT");
        result.NegNightToken.ShouldBe("-NIGHT");
    }

    [Test]
    public async Task BlankSettingValue_FallsBackToDefault()
    {
        SetSetting(AppSettings.SCHEDULE_COMMAND_KEYWORD_FREE, "   ");

        var result = await _provider.GetAsync();

        result.FreeToken.ShouldBe("FREE");
    }

    [Test]
    public async Task ConfiguredValue_OverridesDefault()
    {
        SetSetting(AppSettings.SCHEDULE_COMMAND_KEYWORD_FREE, "URLAUB");
        SetSetting(AppSettings.SCHEDULE_COMMAND_KEYWORD_NEG_NIGHT, "KEINE_NACHT");

        var result = await _provider.GetAsync();

        result.FreeToken.ShouldBe("URLAUB");
        result.NegNightToken.ShouldBe("KEINE_NACHT");
        result.EarlyToken.ShouldBe("EARLY");
    }

    [Test]
    public async Task ValidTokens_ReflectsConfiguredSet()
    {
        SetSetting(AppSettings.SCHEDULE_COMMAND_KEYWORD_FREE, "URLAUB");

        var result = await _provider.GetAsync();

        result.ValidTokens.ShouldContain("URLAUB");
        result.ValidTokens.ShouldNotContain("FREE");
        result.ValidTokens.Count.ShouldBe(8);
    }

    [Test]
    public async Task CollidingTokens_LogsWarning()
    {
        SetSetting(AppSettings.SCHEDULE_COMMAND_KEYWORD_EARLY, "SAME");
        SetSetting(AppSettings.SCHEDULE_COMMAND_KEYWORD_LATE, "SAME");

        await _provider.GetAsync();

        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task DistinctTokens_DoesNotLogWarning()
    {
        var result = await _provider.GetAsync();

        result.ValidTokens.Count.ShouldBe(8);
        _logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    private void SetSetting(string type, string value) => _settingsValues[type] = value;
}
