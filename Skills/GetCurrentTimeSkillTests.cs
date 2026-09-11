// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_current_time: an explicit UserTimezone on the context wins, an invalid one falls
/// through to the company's configured zone (never TimeZoneInfo.Local, never a hard-coded regional
/// default), and with no UserTimezone at all the company zone is used directly.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetCurrentTimeSkillTests
{
    private static readonly DateTimeOffset FixedUtcInstant = new(2026, 6, 27, 23, 30, 0, TimeSpan.Zero);

    private static SkillExecutionContext Ctx(string? userTimezone = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        UserTimezone = userTimezone
    };

    private static GetCurrentTimeSkill CreateSkill(FixedCompanyClock companyClock)
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(FixedUtcInstant);
        return new GetCurrentTimeSkill(new EffectiveTimeZoneResolver(companyClock), timeProvider);
    }

    [Test]
    public async Task ExecuteAsync_ExplicitUserTimezone_UsesItOverTheCompanyZone()
    {
        var companyClock = new FixedCompanyClock(FixedUtcInstant, TimeZoneInfo.Utc);
        var skill = CreateSkill(companyClock);

        var result = await skill.ExecuteAsync(Ctx("Asia/Tokyo"), new Dictionary<string, object> { ["format"] = "date" });

        result.Success.ShouldBeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("2026-06-28");
        json.ShouldContain("Asia/Tokyo");
    }

    [Test]
    public async Task ExecuteAsync_NoUserTimezone_FallsBackToCompanyZone()
    {
        var companyClock = new FixedCompanyClock(FixedUtcInstant, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var skill = CreateSkill(companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["format"] = "date" });

        result.Success.ShouldBeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("2026-06-28");
        json.ShouldContain("Asia/Kolkata");
    }

    [Test]
    public async Task ExecuteAsync_InvalidUserTimezone_FallsThroughToCompanyZone_NeverServerLocal()
    {
        var companyClock = new FixedCompanyClock(FixedUtcInstant, TimeZoneInfo.FindSystemTimeZoneById("America/St_Johns"));
        var skill = CreateSkill(companyClock);

        var result = await skill.ExecuteAsync(Ctx("Not/AZone"), new Dictionary<string, object> { ["format"] = "date" });

        result.Success.ShouldBeTrue();
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        json.ShouldContain("America/St_Johns");
    }
}
