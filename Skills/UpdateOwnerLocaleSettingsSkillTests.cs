// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_owner_locale_settings: persists country/state/timeZone/calendarId
/// independently, mirrors country/state into the globalCalendar* keys, adds when the setting
/// row does not exist yet and updates when it does, and rejects a call with no parameters.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateOwnerLocaleSettingsSkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateOwnerLocaleSettingsSkill _skill = null!;
    private Dictionary<string, SettingsModel> _persistedSettings = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _persistedSettings = new Dictionary<string, SettingsModel>();
        _settingsRepository.AddSetting(Arg.Do<SettingsModel>(s => _persistedSettings[s.Type] = s))
            .Returns(ci => ci.Arg<SettingsModel>());
        _settingsRepository.PutSetting(Arg.Do<SettingsModel>(s => _persistedSettings[s.Type] = s))
            .Returns(ci => ci.Arg<SettingsModel>());
        _settingsRepository.GetSettingNoTracking(Arg.Any<string>())
            .Returns(ci => _persistedSettings.TryGetValue(ci.Arg<string>(), out var s) ? s : null);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());

        _skill = new UpdateOwnerLocaleSettingsSkill(_settingsRepository, _unitOfWork);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    [Test]
    public async Task NoParameters_ReturnsError_NoWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsModel>());
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<SettingsModel>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task CountryOnly_PersistsCountryAndMirroredGlobalCalendarCountry()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH"
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.APP_ADDRESS_COUNTRY && s.Value == "CH"));
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingKeys.GlobalCalendarCountry && s.Value == "CH"));
        await _settingsRepository.DidNotReceive().AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.APP_ADDRESS_STATE));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task StateOnly_PersistsStateAndMirroredGlobalCalendarState()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["state"] = "ZH"
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.APP_ADDRESS_STATE && s.Value == "ZH"));
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingKeys.GlobalCalendarState && s.Value == "ZH"));
    }

    [Test]
    public async Task TimeZoneOnly_PersistsTimeZone_DoesNotTouchCalendarSelection()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["timeZone"] = "Europe/Zurich"
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.APP_ADDRESS_TIMEZONE && s.Value == "Europe/Zurich"));
        await _settingsRepository.DidNotReceive().AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingKeys.GlobalCalendarSelectionId));
    }

    [Test]
    public async Task TimeZone_WindowsId_IsNormalizedToIana_BeforePersisting()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["timeZone"] = "W. Europe Standard Time"
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.APP_ADDRESS_TIMEZONE && s.Value == "Europe/Berlin"));
    }

    [Test]
    public async Task TimeZone_Invalid_ReturnsError_NothingWritten()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["timeZone"] = "Not/AZone"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Invalid time zone");
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsModel>());
        await _settingsRepository.DidNotReceive().PutSetting(Arg.Any<SettingsModel>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task TimeZone_Invalid_AlongsideValidCountry_WritesNothingAtAll()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["timeZone"] = "Not/AZone"
        });

        result.Success.ShouldBeFalse();
        await _settingsRepository.DidNotReceive().AddSetting(Arg.Any<SettingsModel>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task CalendarIdOnly_PersistsGlobalCalendarSelectionId()
    {
        var calendarId = Guid.NewGuid().ToString();

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["calendarId"] = calendarId
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingKeys.GlobalCalendarSelectionId && s.Value == calendarId));
    }

    [Test]
    public async Task ExistingSetting_UsesPutInsteadOfAdd()
    {
        _settingsRepository.GetSetting(SettingsConstants.APP_ADDRESS_TIMEZONE)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingsConstants.APP_ADDRESS_TIMEZONE, Value = "Europe/Vienna" });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["timeZone"] = "Europe/Zurich"
        });

        result.Success.ShouldBeTrue();
        await _settingsRepository.Received(1).PutSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.APP_ADDRESS_TIMEZONE && s.Value == "Europe/Zurich"));
        await _settingsRepository.DidNotReceive().AddSetting(
            Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.APP_ADDRESS_TIMEZONE));
    }

    [Test]
    public async Task AllFourFields_PersistsAll_ReportsUpdatedFields()
    {
        var calendarId = Guid.NewGuid().ToString();

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["state"] = "ZH",
            ["timeZone"] = "Europe/Zurich",
            ["calendarId"] = calendarId
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("country");
        result.Message.ShouldContain("state");
        result.Message.ShouldContain("timeZone");
        result.Message.ShouldContain("calendarId");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ReturnsError_WhenDatabaseVerificationFails()
    {
        _settingsRepository.GetSettingNoTracking(Arg.Any<string>())
            .Returns((SettingsModel?)null);

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["timeZone"] = "Europe/Zurich"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task ReturnsError_WhenPersistedValueDiffers()
    {
        _settingsRepository.GetSettingNoTracking(SettingsConstants.APP_ADDRESS_TIMEZONE)
            .Returns(new SettingsModel
            {
                Id = Guid.NewGuid(),
                Type = SettingsConstants.APP_ADDRESS_TIMEZONE,
                Value = "Europe/Vienna"
            });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["timeZone"] = "Europe/Zurich"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task SuccessMessage_CarriesVerifiedMarker()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["country"] = "CH"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("verified");
    }
}
