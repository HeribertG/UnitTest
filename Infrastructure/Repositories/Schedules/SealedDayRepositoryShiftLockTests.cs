// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SealedDayRepository.IsDayLockedForShiftAsync against an in-memory DataBaseContext: the
/// restore guard must see a global seal and a group seal whose group contains the shift, even when the
/// client currently has no live Work on that day (his only Work is the deleted one being restored) -
/// the client-based IsDayLockedAsync would report the day as free in exactly that situation.
/// </summary>

using Klacks.Api.Domain.Models.Associations;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class SealedDayRepositoryShiftLockTests
{
    private DataBaseContext _context = null!;
    private SealedDayRepository _sut = null!;

    private readonly Guid _shiftId = Guid.NewGuid();
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 10);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new SealedDayRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task IsDayLockedForShiftAsync_NoSeal_ReturnsFalse()
    {
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId });
        await _context.SaveChangesAsync();

        var locked = await _sut.IsDayLockedForShiftAsync(_date, _shiftId);

        locked.ShouldBeFalse();
    }

    [Test]
    public async Task IsDayLockedForShiftAsync_GlobalSeal_ReturnsTrue()
    {
        _context.SealedDay.Add(new SealedDay { Id = Guid.NewGuid(), Date = _date, GroupId = null });
        await _context.SaveChangesAsync();

        var locked = await _sut.IsDayLockedForShiftAsync(_date, _shiftId);

        locked.ShouldBeTrue();
    }

    [Test]
    public async Task IsDayLockedForShiftAsync_GroupSealContainingTheShift_ReturnsTrue_WithoutAnyLiveWork()
    {
        _context.SealedDay.Add(new SealedDay { Id = Guid.NewGuid(), Date = _date, GroupId = _groupId });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId });
        await _context.SaveChangesAsync();

        var locked = await _sut.IsDayLockedForShiftAsync(_date, _shiftId);

        locked.ShouldBeTrue();
    }

    [Test]
    public async Task IsDayLockedForShiftAsync_GroupSealOfAnotherGroup_ReturnsFalse()
    {
        _context.SealedDay.Add(new SealedDay { Id = Guid.NewGuid(), Date = _date, GroupId = Guid.NewGuid() });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId });
        await _context.SaveChangesAsync();

        var locked = await _sut.IsDayLockedForShiftAsync(_date, _shiftId);

        locked.ShouldBeFalse();
    }

    [Test]
    public async Task IsDayLockedForShiftAsync_GroupSealOnAnotherDay_ReturnsFalse()
    {
        _context.SealedDay.Add(new SealedDay { Id = Guid.NewGuid(), Date = _date.AddDays(1), GroupId = _groupId });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId });
        await _context.SaveChangesAsync();

        var locked = await _sut.IsDayLockedForShiftAsync(_date, _shiftId);

        locked.ShouldBeFalse();
    }

    [Test]
    public async Task IsDayLockedForShiftAsync_DeletedGroupItem_DoesNotBindTheShift()
    {
        _context.SealedDay.Add(new SealedDay { Id = Guid.NewGuid(), Date = _date, GroupId = _groupId });
        _context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = _groupId, ShiftId = _shiftId, IsDeleted = true });
        await _context.SaveChangesAsync();

        var locked = await _sut.IsDayLockedForShiftAsync(_date, _shiftId);

        locked.ShouldBeFalse();
    }
}
