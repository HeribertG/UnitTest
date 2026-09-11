// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleRecurringTaskSkill time-zone resolution: an explicit timeZoneId wins, then the
/// user's context timezone, then the company's configured time zone (ICompanyClock) - never a
/// hard-coded regional default, and no longer the app owner's separate GlobalCalendarCountry setting,
/// which was a third, divergent "company zone" source. Also covers the pure CountryTimeZones map and the
/// guard that refuses to freeze an empty permission set for a skill action.
/// The second block covers the way OUT of a pause: the skill is the only surface that can set the
/// per-task irreversible opt-in at all, and re-applying an existing task by name has to lift the pause -
/// otherwise the note telling the owner to fix the cause points at a state nothing can leave.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ScheduleRecurringTaskSkillTests
{
    private IScheduledTaskRepository _repository = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private FixedCompanyClock _companyClock = null!;
    private ScheduleRecurringTaskSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IScheduledTaskRepository>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        _skill = new ScheduleRecurringTaskSkill(
            _repository, _skillRegistry, _riskClassifier, new EffectiveTimeZoneResolver(_companyClock));
    }

    private void CompanyZone(string ianaId) => _companyClock.TimeZone = TimeZoneInfo.FindSystemTimeZoneById(ianaId);

    private static SkillExecutionContext Ctx(string? userTimezone = null, IReadOnlyList<string>? permissions = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = permissions ?? new List<string>(),
        UserTimezone = userTimezone
    };

    private static Dictionary<string, object> SkillParams() => new()
    {
        ["name"] = "weekly report",
        ["cronExpression"] = "0 8 * * 1",
        ["actionType"] = "skill",
        ["skillName"] = "list_clients",
        ["apply"] = true
    };

    private void KnownHarmlessSkill(string name)
    {
        _skillRegistry.GetSkillByName(name).Returns(new SkillDescriptor(
            name, "test skill", SkillCategory.Query,
            Array.Empty<SkillParameter>(), Array.Empty<string>(), Array.Empty<LLMCapability>(), null));
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(SkillRiskClass.ReadOnly);
    }

    private static Dictionary<string, object> ReminderParams(string? timeZoneId = null)
    {
        var p = new Dictionary<string, object>
        {
            ["name"] = "weekly check",
            ["cronExpression"] = "0 8 * * 1",
            ["actionType"] = "reminder",
            ["messageText"] = "check coverage",
            ["apply"] = true
        };
        if (timeZoneId is not null)
        {
            p["timeZoneId"] = timeZoneId;
        }

        return p;
    }

    [Test]
    public async Task NoTimeZone_NoUserTimezone_UsesTheCompanyZone_Switzerland_ZurichZone()
    {
        CompanyZone("Europe/Zurich");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Zurich"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoTimeZone_NoUserTimezone_UsesTheCompanyZone_India_KolkataZone()
    {
        CompanyZone("Asia/Kolkata");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Asia/Kolkata"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitTimeZone_OverridesTheCompanyZone()
    {
        CompanyZone("Europe/Zurich");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams("America/New_York"));

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "America/New_York"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitTimeZone_OverridesTheUserTimezoneToo()
    {
        var result = await _skill.ExecuteAsync(Ctx("Europe/Vienna"), ReminderParams("America/New_York"));

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "America/New_York"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoExplicitTimeZone_UserTimezoneOverridesTheCompanyZone()
    {
        CompanyZone("Europe/Zurich");

        var result = await _skill.ExecuteAsync(Ctx("Europe/Vienna"), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Vienna"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExplicitWindowsTimeZone_IsNormalizedToIana_BeforePersisting()
    {
        CompanyZone("Europe/Zurich");

        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams("W. Europe Standard Time"));

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Berlin"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoExplicitTimeZone_InvalidUserTimezone_FallsThroughToTheCompanyZone()
    {
        CompanyZone("Europe/Zurich");

        var result = await _skill.ExecuteAsync(Ctx("Not/AZone"), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.TimeZoneId == "Europe/Zurich"), Arg.Any<CancellationToken>());
    }

    [TestCase("CH", "Europe/Zurich")]
    [TestCase("ch", "Europe/Zurich")]
    [TestCase(" DE ", "Europe/Berlin")]
    [TestCase("AT", "Europe/Vienna")]
    [TestCase("LI", "Europe/Vaduz")]
    public void CountryTimeZones_Resolve_KnownCodes(string code, string expected)
    {
        CountryTimeZones.Resolve(code).ShouldBe(expected);
    }

    [TestCase("US")]
    [TestCase("")]
    [TestCase(null)]
    public void CountryTimeZones_Resolve_UnknownOrEmpty_ReturnsNull(string? code)
    {
        CountryTimeZones.Resolve(code).ShouldBeNull();
    }

    [Test]
    public async Task SkillAction_WithoutPermissions_IsRefusedAndNothingIsPersisted()
    {
        KnownHarmlessSkill("list_clients");

        var result = await _skill.ExecuteAsync(Ctx(), SkillParams());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("permission check");
        await _repository.DidNotReceive().AddAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SkillAction_WithPermissions_FreezesThem()
    {
        KnownHarmlessSkill("list_clients");

        var result = await _skill.ExecuteAsync(
            Ctx(permissions: new[] { "Authorised", "CanViewClients" }), SkillParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.OwnerPermissionsCsv == "Authorised,CanViewClients"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reminder_WithoutPermissions_IsStillAllowed()
    {
        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }

    private ScheduledTask ExistingTask(Guid ownerUserId, string name, string? pausedReason = null)
    {
        var task = new ScheduledTask
        {
            Id = Guid.NewGuid(),
            Name = name,
            CronExpression = "0 6 * * 1",
            TimeZoneId = "Europe/Zurich",
            ActionType = ScheduledTaskActionTypes.Reminder,
            MessageText = "old text",
            OwnerUserId = ownerUserId,
            OwnerUserName = "tester",
            IsEnabled = true,
            RunCount = 4
        };

        if (pausedReason is not null)
        {
            task.Pause(pausedReason);
        }

        _repository.GetByOwnerAndNameAsync(ownerUserId, name, Arg.Any<CancellationToken>()).Returns(task);
        return task;
    }

    [Test]
    public async Task ReApplyingAPausedTask_LiftsThePauseAndDropsItsReason()
    {
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check", "irreversible skill without the opt-in");

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Success.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
        existing.PausedReason.ShouldBeNull();
        await _repository.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReApplyingAPausedTask_TellsTheUserItIsRunningAgain()
    {
        var context = Ctx();
        ExistingTask(context.UserId, "weekly check", "irreversible skill without the opt-in");

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Message!.ShouldContain("paused and is running again");
    }

    [Test]
    public async Task ReApplyingATaskThatWasNotPaused_SaysNothingAboutAPause()
    {
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check");

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Success.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
        result.Message!.ShouldNotContain("paused");
    }

    [Test]
    public async Task IrreversibleOptIn_DefaultsToOff_OnANewTask()
    {
        var result = await _skill.ExecuteAsync(Ctx(), ReminderParams());

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => !t.AllowIrreversibleUnattended), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IrreversibleOptIn_IsPersisted_OnANewTask()
    {
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = true;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        await _repository.Received(1).AddAsync(
            Arg.Is<ScheduledTask>(t => t.AllowIrreversibleUnattended), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IrreversibleOptIn_IsPersisted_WhenAnExistingTaskIsReApplied()
    {
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check", "irreversible skill without the opt-in");
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = true;

        var result = await _skill.ExecuteAsync(context, parameters);

        result.Success.ShouldBeTrue();
        existing.AllowIrreversibleUnattended.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
    }

    [Test]
    public async Task IrreversibleOptIn_SurvivesAReApplyThatOmitsIt_SoTheResumeAdviceDoesNotLoop()
    {
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check", "autonomy level too low");
        existing.AllowIrreversibleUnattended = true;

        var result = await _skill.ExecuteAsync(context, ReminderParams());

        result.Success.ShouldBeTrue();
        existing.AllowIrreversibleUnattended.ShouldBeTrue();
        existing.IsPaused.ShouldBeFalse();
    }

    [Test]
    public async Task IrreversibleOptIn_CanStillBeSwitchedOffExplicitly()
    {
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check");
        existing.AllowIrreversibleUnattended = true;
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = false;

        var result = await _skill.ExecuteAsync(context, parameters);

        result.Success.ShouldBeTrue();
        existing.AllowIrreversibleUnattended.ShouldBeFalse();
    }

    [Test]
    public async Task Preview_OfAnExistingTask_ShowsTheOptInItActuallyCarries()
    {
        var context = Ctx();
        var existing = ExistingTask(context.UserId, "weekly check");
        existing.AllowIrreversibleUnattended = true;
        var parameters = ReminderParams();
        parameters["apply"] = false;

        var result = await _skill.ExecuteAsync(context, parameters);

        result.Success.ShouldBeTrue();
        System.Text.Json.JsonSerializer.Serialize(result.Data)
            .ShouldContain("\"allowIrreversibleUnattended\":true");
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IrreversibleOptIn_AppearsInThePreviewBeforeAnythingIsSaved()
    {
        var parameters = ReminderParams();
        parameters["allowIrreversibleUnattended"] = true;
        parameters["apply"] = false;

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        System.Text.Json.JsonSerializer.Serialize(result.Data)
            .ShouldContain("\"allowIrreversibleUnattended\":true");
        await _repository.DidNotReceive().AddAsync(Arg.Any<ScheduledTask>(), Arg.Any<CancellationToken>());
    }
}
