// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListRecentExportsSkillTests
{
    private static readonly DateTimeOffset CompanyNow = new(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    [Test]
    public async Task List_WithExplicitRange_PassesQueryAndReturnsEntries()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetExportLogQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExportLogDto>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Format = "csv",
                    StartDate = new DateOnly(2026, 5, 1),
                    EndDate = new DateOnly(2026, 5, 31),
                    FileName = "order-export_MIG-05.csv",
                    FileSize = 2048,
                    RecordCount = 20,
                    ExportedAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
                    ExportedBy = "user-id",
                    ExportedByName = "Hans Muster"
                }
            });
        var skill = new ListRecentExportsSkill(mediator, new FixedCompanyClock(CompanyNow));

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["fromDate"] = "2026-05-01",
            ["untilDate"] = "2026-05-31"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("1 export");
        await mediator.Received(1).Send(
            Arg.Is<GetExportLogQuery>(q =>
                q.From == new DateOnly(2026, 5, 1) &&
                q.To == new DateOnly(2026, 5, 31)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_WithoutRange_DefaultsToLastYear()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetExportLogQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExportLogDto>());
        var skill = new ListRecentExportsSkill(mediator, new FixedCompanyClock(CompanyNow));

        var expectedTo = DateOnly.FromDateTime(CompanyNow.UtcDateTime);
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<GetExportLogQuery>(q =>
                q.To == expectedTo &&
                q.From == q.To.AddDays(-365)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_FromAfterUntil_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new ListRecentExportsSkill(mediator, new FixedCompanyClock(CompanyNow));

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["fromDate"] = "2026-06-01",
            ["untilDate"] = "2026-05-01"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("fromDate must not be after untilDate");
        await mediator.DidNotReceive().Send(
            Arg.Any<GetExportLogQuery>(), Arg.Any<CancellationToken>());
    }
}
