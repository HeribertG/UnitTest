// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_holidays_for_period, validate_holiday_overlap and import_calendar_rules.
/// Uses an in-memory ISettingsRepository mock with two simple Swiss rules so the
/// HolidaysListCalculator path runs end-to-end.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class HolidaySkillTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;

    [SetUp]
    public void Setup()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = new FixedCompanyClock(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings", "CanViewSettings" }
    };

    private static CalendarRule NewYearRule()
    {
        var name = new MultiLanguage();
        name.SetValue("de", "Neujahr");
        name.SetValue("en", "Neujahr");
        return new CalendarRule
        {
            Id = Guid.NewGuid(),
            Country = "CH",
            State = string.Empty,
            Rule = "01.01",
            SubRule = string.Empty,
            Name = name,
            Description = MultiLanguage.Empty(),
            IsMandatory = true,
            IsPaid = true
        };
    }

    private static CalendarRule LaborDayRule()
    {
        var name = new MultiLanguage();
        name.SetValue("de", "Tag der Arbeit");
        return new CalendarRule
        {
            Id = Guid.NewGuid(),
            Country = "CH",
            State = "BE",
            Rule = "05.01",
            SubRule = string.Empty,
            Name = name,
            Description = MultiLanguage.Empty(),
            IsMandatory = true,
            IsPaid = true
        };
    }

    [Test]
    public async Task ListHolidaysForPeriod_ReturnsHolidaysInRange()
    {
        var skill = new ListHolidaysForPeriodSkill(_settingsRepository);
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule> { NewYearRule(), LaborDayRule() });
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["state"] = "BE",
            ["fromDate"] = "2026-01-01",
            ["untilDate"] = "2026-12-31"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("2 holiday(s)"));
    }

    [Test]
    public async Task ListHolidaysForPeriod_RejectsInvertedRange()
    {
        var skill = new ListHolidaysForPeriodSkill(_settingsRepository);
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["fromDate"] = "2026-12-31",
            ["untilDate"] = "2026-01-01"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("on or after"));
    }

    [Test]
    public async Task ValidateHolidayOverlap_ReturnsTrueForNewYear()
    {
        var skill = new ValidateHolidayOverlapSkill(_settingsRepository);
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule> { NewYearRule() });
        var parameters = new Dictionary<string, object>
        {
            ["date"] = "2026-01-01",
            ["country"] = "CH"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Neujahr"));
    }

    [Test]
    public async Task ValidateHolidayOverlap_ReturnsFalseForNormalDay()
    {
        var skill = new ValidateHolidayOverlapSkill(_settingsRepository);
        _settingsRepository.GetCalendarRuleList().Returns(new List<CalendarRule> { NewYearRule() });
        var parameters = new Dictionary<string, object>
        {
            ["date"] = "2026-03-15",
            ["country"] = "CH"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("NOT a holiday"));
    }

    [Test]
    public async Task ImportCalendarRules_RejectsInvalidJson()
    {
        var skill = new ImportCalendarRulesSkill(_settingsRepository, _unitOfWork, _companyClock);
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["rulesJson"] = "{ this is not valid json }"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("valid JSON"));
        _settingsRepository.DidNotReceive().AddCalendarRule(Arg.Any<CalendarRule>());
    }

    [Test]
    public async Task ImportCalendarRules_RejectsEmptyArray()
    {
        var skill = new ImportCalendarRulesSkill(_settingsRepository, _unitOfWork, _companyClock);
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["rulesJson"] = "[]"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("no rules"));
    }

    [Test]
    public async Task ImportCalendarRules_AddsValidRulesAndPersists()
    {
        var skill = new ImportCalendarRulesSkill(_settingsRepository, _unitOfWork, _companyClock);
        var rulesJson = """
            [
              { "rule": "01.01", "nameDe": "Neujahr", "nameEn": "New Year" },
              { "rule": "08.01", "nameDe": "Bundesfeier" }
            ]
            """;
        var parameters = new Dictionary<string, object>
        {
            ["country"] = "CH",
            ["state"] = "BE",
            ["rulesJson"] = rulesJson
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _settingsRepository.Received(2).AddCalendarRule(Arg.Any<CalendarRule>());
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
