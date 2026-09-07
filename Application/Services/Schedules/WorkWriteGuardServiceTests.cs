// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for WorkWriteGuardService, the pre-write checks shared by the Work create and restore
/// handlers: sporadic-shift capacity (day and range) and the hard-blocking conflict class that can never
/// be reported after the fact. Scenario writes bypass the hard-blocking check like the day-lock guard.
/// </summary>

using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Shifts;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WorkWriteGuardServiceTests
{
    private const string ShiftName = "Night watch";

    private IShiftRepository _shiftRepository = null!;
    private IWorkRepository _workRepository = null!;
    private IPreCommitConflictChecker _conflictChecker = null!;
    private WorkWriteGuardService _sut = null!;

    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 10);

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _workRepository = Substitute.For<IWorkRepository>();
        _conflictChecker = Substitute.For<IPreCommitConflictChecker>();
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(PreCommitCheckResult.Empty);
        _sut = new WorkWriteGuardService(_shiftRepository, _workRepository, _conflictChecker);
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_NonSporadicShift_NeverQueriesCapacity()
    {
        _shiftRepository.GetSporadicInfoAsync(_shiftId, Arg.Any<CancellationToken>())
            .Returns(SporadicInfo(isSporadic: false, sumEmployees: 1, quantity: 1));

        await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        await _workRepository.DidNotReceiveWithAnyArgs().GetSporadicCapacityUsageAsync(
            default, default, default, default, default, default, default);
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_DayFullyBooked_Throws()
    {
        GivenSporadicUsage(engagedAtDay: 2, distinctBookedDays: 1, sumEmployees: 2, quantity: 5);

        var act = async () => await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<ConflictException>();
        exception.Message.ShouldContain("fully booked");
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_RangeQuantityExhaustedOnNewDay_Throws()
    {
        GivenSporadicUsage(engagedAtDay: 0, distinctBookedDays: 5, sumEmployees: 2, quantity: 5);

        var act = async () => await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<ConflictException>();
        exception.Message.ShouldContain("range capacity");
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_CapacityLeft_Passes()
    {
        GivenSporadicUsage(engagedAtDay: 1, distinctBookedDays: 5, sumEmployees: 2, quantity: 5);

        await _sut.EnsureNoSporadicConflictAsync(NewWork(), CancellationToken.None);
    }

    [Test]
    public async Task EnsureNoSporadicConflictAsync_ExcludesTheWorkItself_WhenItHasAnId()
    {
        var work = NewWork();
        work.Id = Guid.NewGuid();
        GivenSporadicUsage(engagedAtDay: 0, distinctBookedDays: 0, sumEmployees: 1, quantity: 1);

        await _sut.EnsureNoSporadicConflictAsync(work, CancellationToken.None);

        await _workRepository.Received(1).GetSporadicCapacityUsageAsync(
            _shiftId, _date, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), work.Id, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_HardBlocking_Throws()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([HardBlocking()]));

        var act = async () => await _sut.EnsureNoHardBlockingConflictAsync(NewWork(), CancellationToken.None);

        await act.ShouldThrowAsync<ConflictException>();
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_CollisionOnly_Passes()
    {
        _conflictChecker.CheckAsync(Arg.Any<IReadOnlyList<PlannedWorkRow>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new PreCommitCheckResult([Collision()]));

        await _sut.EnsureNoHardBlockingConflictAsync(NewWork(), CancellationToken.None);
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_ScenarioWrite_SkipsTheCheck()
    {
        var work = NewWork();
        work.AnalyseToken = Guid.NewGuid();

        await _sut.EnsureNoHardBlockingConflictAsync(work, CancellationToken.None);

        await _conflictChecker.DidNotReceiveWithAnyArgs().CheckAsync(default!, default(Guid?), default);
    }

    [Test]
    public async Task EnsureNoHardBlockingConflictAsync_ChecksExactlyThePlannedRow()
    {
        var work = NewWork();

        await _sut.EnsureNoHardBlockingConflictAsync(work, CancellationToken.None);

        await _conflictChecker.Received(1).CheckAsync(
            Arg.Is<IReadOnlyList<PlannedWorkRow>>(rows =>
                rows.Count == 1
                && rows[0].ClientId == _clientId
                && rows[0].Date == _date
                && rows[0].StartTime == work.StartTime
                && rows[0].EndTime == work.EndTime
                && rows[0].ShiftId == _shiftId),
            null,
            Arg.Any<CancellationToken>());
    }

    private Work NewWork() => new()
    {
        ClientId = _clientId,
        ShiftId = _shiftId,
        CurrentDate = _date,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0)
    };

    private void GivenSporadicUsage(int engagedAtDay, int distinctBookedDays, int sumEmployees, int quantity)
    {
        _shiftRepository.GetSporadicInfoAsync(_shiftId, Arg.Any<CancellationToken>())
            .Returns(SporadicInfo(isSporadic: true, sumEmployees, quantity));
        _workRepository.GetSporadicCapacityUsageAsync(
                _shiftId, _date, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new SporadicCapacityUsage(engagedAtDay, distinctBookedDays));
    }

    private SporadicShiftInfo SporadicInfo(bool isSporadic, int sumEmployees, int quantity) =>
        new(ShiftName, isSporadic, ShiftSporadic.Month, _date.AddMonths(-1), null, sumEmployees, quantity);

    private ScheduleValidationNotificationDto HardBlocking() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _clientId,
        Date = _date,
        Comment = "qualification-missing"
    };

    private ScheduleValidationNotificationDto Collision() => new()
    {
        Type = ScheduleValidationType.Error,
        ClientId = _clientId,
        Date = _date,
        Comment = ScheduleValidationKeys.Collision
    };
}
