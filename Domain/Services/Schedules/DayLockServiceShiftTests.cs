// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DayLockService.EnsureNotLockedForShiftAsync, the shift-based seal guard used by the Work
/// restore: same message and same scenario exemption as the client-based guard, but resolved through the
/// shift's group membership because a deleted Work does not bind its client to any group.
/// </summary>

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public sealed class DayLockServiceShiftTests
{
    private static readonly DateOnly Day = new(2026, 5, 11);
    private static readonly Guid ShiftId = Guid.NewGuid();

    private ISealedDayRepository _repository = null!;
    private DayLockService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISealedDayRepository>();
        _sut = new DayLockService(_repository);
    }

    [Test]
    public async Task EnsureNotLockedForShiftAsync_NotSealed_Passes()
    {
        _repository.IsDayLockedForShiftAsync(Day, ShiftId, Arg.Any<CancellationToken>()).Returns(false);

        await _sut.EnsureNotLockedForShiftAsync(Day, ShiftId, null);
    }

    [Test]
    public async Task EnsureNotLockedForShiftAsync_Sealed_ThrowsWithTheDate()
    {
        _repository.IsDayLockedForShiftAsync(Day, ShiftId, Arg.Any<CancellationToken>()).Returns(true);

        var act = async () => await _sut.EnsureNotLockedForShiftAsync(Day, ShiftId, null);

        var exception = await act.ShouldThrowAsync<InvalidRequestException>();
        exception.Message.ShouldContain(Day.ToString("yyyy-MM-dd"));
    }

    [Test]
    public async Task EnsureNotLockedForShiftAsync_ScenarioWrite_IsExemptAndNeverQueried()
    {
        await _sut.EnsureNotLockedForShiftAsync(Day, ShiftId, Guid.NewGuid());

        await _repository.DidNotReceiveWithAnyArgs().IsDayLockedForShiftAsync(default, default, default);
    }
}
