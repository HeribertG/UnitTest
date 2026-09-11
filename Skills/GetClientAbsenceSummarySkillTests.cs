// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetClientAbsenceSummarySkill — verifies per-type day counts for planned and
/// booked absences, year clipping of placeholders spanning the year boundary, the weighted value,
/// and error handling for unknown clients.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetClientAbsenceSummarySkillTests
{
    private IClientRepository _clientRepository = null!;
    private IBreakPlaceholderRepository _breakPlaceholderRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private GetClientAbsenceSummarySkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid VacationId = Guid.NewGuid();
    private static readonly Guid SickId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _breakPlaceholderRepository = Substitute.For<IBreakPlaceholderRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();

        _clientRepository.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Hans", Name = "Muster" });
        _absenceRepository.List().Returns(new List<Absence>
        {
            new() { Id = VacationId, Name = new MultiLanguage { De = "Ferien" }, DefaultValue = 1.0 },
            new() { Id = SickId, Name = new MultiLanguage { De = "Krankheit" }, DefaultValue = 1.0 }
        });
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder>());
        _breakRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>());

        _skill = new GetClientAbsenceSummarySkill(
            _clientRepository, _breakPlaceholderRepository, _breakRepository, _absenceRepository,
            new FixedCompanyClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero)));
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(int year = 2026) => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["year"] = year
    };

    [Test]
    public async Task PlannedAndBooked_AreCountedPerType()
    {
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ClientId = ClientId,
                    AbsenceId = VacationId,
                    From = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
                    Until = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc)
                }
            });
        _breakRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>
            {
                new() { Id = Guid.NewGuid(), ClientId = ClientId, AbsenceId = SickId, CurrentDate = new DateOnly(2026, 3, 2) },
                new() { Id = Guid.NewGuid(), ClientId = ClientId, AbsenceId = SickId, CurrentDate = new DateOnly(2026, 3, 3) }
            });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Ferien 5d (5 planned / 0 booked)");
        result.Message.ShouldContain("Krankheit 2d (0 planned / 2 booked)");
        result.Message.ShouldContain("7 absence day(s) in 2026");
    }

    [Test]
    public async Task PlaceholderSpanningYearBoundary_IsClippedToYear()
    {
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ClientId = ClientId,
                    AbsenceId = VacationId,
                    From = new DateTime(2025, 12, 29, 0, 0, 0, DateTimeKind.Utc),
                    Until = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc)
                }
            });

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Ferien 3d");
    }

    [Test]
    public async Task NoAbsences_ReportsCleanYear()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("no absences in 2026");
    }

    [Test]
    public async Task UnknownClient_ReturnsError()
    {
        _clientRepository.Get(ClientId).Returns(Task.FromResult<Client?>(null));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }
}
