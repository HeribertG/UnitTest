// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_erp_import_status: reports the drop point's enabled state, cron schedule
/// and file counts, and formats the next scheduled run from the persisted setting.
/// </summary>

using Klacks.Api.Application.DTOs.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetErpImportStatusSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    private static ErpDropPointFilesResource EmptyFiles() => new();

    [Test]
    public async Task Disabled_ReportsDisabled_WithoutScheduleDetails()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { IsEnabled = false });
        mediator.Send(Arg.Any<GetDefaultFilesQuery>(), Arg.Any<CancellationToken>())
            .Returns(EmptyFiles());
        var settingsReader = Substitute.For<ISettingsReader>();
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var skill = new GetErpImportStatusSkill(
            mediator, settingsReader, companyClock, NullLogger<GetErpImportStatusSkill>.Instance);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("ERP import is currently disabled.");
    }

    [Test]
    public async Task Enabled_ReportsSchedule_NextRun_AndFileCounts()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { IsEnabled = true });
        mediator.Send(Arg.Any<GetDefaultFilesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointFilesResource
            {
                Pending = [new ErpDropPointFileResource { Key = "a", FileName = "a.xml" }],
                Processed = [new ErpDropPointFileResource { Key = "b", FileName = "b.xml" }],
                Error =
                [
                    new ErpDropPointFileResource { Key = "c", FileName = "c.xml", ErrorReason = "boom" }
                ]
            });
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSetting(ErpImportSettingsTypes.CronExpression)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronExpression, Value = "0 * * * *" });
        settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Zurich" });
        settingsReader.GetSetting(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.NextRunUtc, Value = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc).ToString("O") });
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
        var skill = new GetErpImportStatusSkill(
            mediator, settingsReader, companyClock, NullLogger<GetErpImportStatusSkill>.Instance);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("enabled");
        result.Message.ShouldContain("1 pending, 1 processed, 1 failed");
    }

    [Test]
    public async Task MissingCronSettings_FallBackToDefaultCronAndCompanyZone()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { IsEnabled = true });
        mediator.Send(Arg.Any<GetDefaultFilesQuery>(), Arg.Any<CancellationToken>())
            .Returns(EmptyFiles());
        var settingsReader = Substitute.For<ISettingsReader>();
        var companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        var skill = new GetErpImportStatusSkill(
            mediator, settingsReader, companyClock, NullLogger<GetErpImportStatusSkill>.Instance);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain(ErpImportSettingsTypes.DefaultCronExpression);
        result.Message.ShouldContain("Asia/Tokyo");
    }
}
