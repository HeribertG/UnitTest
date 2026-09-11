// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_user_context: an explicit, valid UserTimezone on the context is reported as-is;
/// an invalid one, or none set at all, falls through to the company's configured zone (never a
/// hard-coded regional default).
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetUserContextSkillTests
{
    private static SkillExecutionContext Ctx(string? userTimezone = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        UserTimezone = userTimezone
    };

    [Test]
    public async Task ExecuteAsync_ExplicitUserTimezone_IsReportedAsIs()
    {
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var skill = new GetUserContextSkill(new EffectiveTimeZoneResolver(companyClock));

        var result = await skill.ExecuteAsync(Ctx("Europe/Vienna"), new Dictionary<string, object>());

        System.Text.Json.JsonSerializer.Serialize(result.Data).ShouldContain("Europe/Vienna");
    }

    [Test]
    public async Task ExecuteAsync_NoUserTimezone_FallsBackToCompanyZone()
    {
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        var skill = new GetUserContextSkill(new EffectiveTimeZoneResolver(companyClock));

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        System.Text.Json.JsonSerializer.Serialize(result.Data).ShouldContain("Asia/Kolkata");
    }

    [Test]
    public async Task ExecuteAsync_InvalidUserTimezone_FallsThroughToCompanyZone()
    {
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/St_Johns"));
        var skill = new GetUserContextSkill(new EffectiveTimeZoneResolver(companyClock));

        var result = await skill.ExecuteAsync(Ctx("Not/AZone"), new Dictionary<string, object>());

        System.Text.Json.JsonSerializer.Serialize(result.Data).ShouldContain("America/St_Johns");
    }
}
