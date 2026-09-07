// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ContainerWorkCascadeService against an in-memory DataBaseContext: verifies that moving a
/// container Work to another client also reassigns its WorkChange (child Work) and Break children, so a
/// cross-client schedule move never orphans them under the old client. The restore side brings back only
/// the children that the same delete took away (same user, same instant within tolerance) and leaves
/// children deleted earlier or by another actor deleted.
/// </summary>
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class ContainerWorkCascadeServiceTests
{
    private DataBaseContext _context = null!;
    private ContainerWorkCascadeService _sut = null!;

    private readonly Guid _sourceClientId = Guid.NewGuid();
    private readonly Guid _targetClientId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 1);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new ContainerWorkCascadeService(_context, NullLogger<ContainerWorkCascadeService>.Instance);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task MoveChildrenAsync_ReassignsChildWorkChange_ToNewClient()
    {
        var parent = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        var workChange = new Work { Id = Guid.NewGuid(), ParentWorkId = parent.Id, ClientId = _sourceClientId, CurrentDate = _date };
        _context.Work.AddRange(parent, workChange);
        await _context.SaveChangesAsync();

        await _sut.MoveChildrenAsync(parent.Id, _date, _targetClientId);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Work.SingleAsync(w => w.Id == workChange.Id);
        reloaded.ClientId.ShouldBe(_targetClientId);
    }

    [Test]
    public async Task MoveChildrenAsync_ReassignsChildBreak_ToNewClient()
    {
        var parent = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        var childBreak = new Break { Id = Guid.NewGuid(), ParentWorkId = parent.Id, ClientId = _sourceClientId, CurrentDate = _date };
        _context.Work.Add(parent);
        _context.Break.Add(childBreak);
        await _context.SaveChangesAsync();

        await _sut.MoveChildrenAsync(parent.Id, _date, _targetClientId);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Break.SingleAsync(b => b.Id == childBreak.Id);
        reloaded.ClientId.ShouldBe(_targetClientId);
    }

    [Test]
    public async Task MoveChildrenAsync_LeavesUnrelatedWork_Untouched()
    {
        var parent = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        var unrelated = new Work { Id = Guid.NewGuid(), ClientId = _sourceClientId, CurrentDate = _date };
        _context.Work.AddRange(parent, unrelated);
        await _context.SaveChangesAsync();

        await _sut.MoveChildrenAsync(parent.Id, _date, _targetClientId);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Work.SingleAsync(w => w.Id == unrelated.Id);
        reloaded.ClientId.ShouldBe(_sourceClientId);
    }

    [Test]
    public async Task RestoreChildrenAsync_RestoresChildWorkAndBreak_DeletedByTheSameDelete()
    {
        var deletedTime = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var parent = DeletedWork(null, deletedTime, DeletedBy);
        var childWork = DeletedWork(parent.Id, deletedTime.AddMilliseconds(2), DeletedBy);
        var childBreak = DeletedBreak(parent.Id, deletedTime.AddMilliseconds(4), DeletedBy);
        _context.Work.AddRange(parent, childWork);
        _context.Break.Add(childBreak);
        await _context.SaveChangesAsync();

        await _sut.RestoreChildrenAsync(parent.Id, deletedTime, DeletedBy);
        await _context.SaveChangesAsync();

        var reloadedWork = await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == childWork.Id);
        var reloadedBreak = await _context.Break.IgnoreQueryFilters().SingleAsync(b => b.Id == childBreak.Id);
        reloadedWork.IsDeleted.ShouldBeFalse();
        reloadedWork.DeletedTime.ShouldBeNull();
        reloadedWork.CurrentUserDeleted.ShouldBeNull();
        reloadedWork.ParentWorkId.ShouldBe(parent.Id);
        reloadedBreak.IsDeleted.ShouldBeFalse();
        reloadedBreak.DeletedTime.ShouldBeNull();
        reloadedBreak.CurrentUserDeleted.ShouldBeNull();
    }

    [Test]
    public async Task RestoreChildrenAsync_LeavesChildrenDeletedOutsideTheTolerance_Deleted()
    {
        var deletedTime = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var parent = DeletedWork(null, deletedTime, DeletedBy);
        var earlier = DeletedWork(parent.Id, deletedTime.AddSeconds(-(WorkRestoreDefaults.SiblingDeleteToleranceSeconds + 1)), DeletedBy);
        var later = DeletedBreak(parent.Id, deletedTime.AddSeconds(WorkRestoreDefaults.SiblingDeleteToleranceSeconds + 1), DeletedBy);
        _context.Work.AddRange(parent, earlier);
        _context.Break.Add(later);
        await _context.SaveChangesAsync();

        await _sut.RestoreChildrenAsync(parent.Id, deletedTime, DeletedBy);
        await _context.SaveChangesAsync();

        (await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == earlier.Id)).IsDeleted.ShouldBeTrue();
        (await _context.Break.IgnoreQueryFilters().SingleAsync(b => b.Id == later.Id)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task RestoreChildrenAsync_LeavesChildrenDeletedByAnotherActor_Deleted()
    {
        var deletedTime = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var parent = DeletedWork(null, deletedTime, DeletedBy);
        var byOther = DeletedWork(parent.Id, deletedTime, "user-2");
        var byNobody = DeletedWork(parent.Id, deletedTime, null);
        _context.Work.AddRange(parent, byOther, byNobody);
        await _context.SaveChangesAsync();

        await _sut.RestoreChildrenAsync(parent.Id, deletedTime, DeletedBy);
        await _context.SaveChangesAsync();

        (await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == byOther.Id)).IsDeleted.ShouldBeTrue();
        (await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == byNobody.Id)).IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task RestoreChildrenAsync_LeavesChildrenOfOtherParents_Deleted()
    {
        var deletedTime = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var parent = DeletedWork(null, deletedTime, DeletedBy);
        var otherParent = DeletedWork(null, deletedTime, DeletedBy);
        var otherChild = DeletedWork(otherParent.Id, deletedTime, DeletedBy);
        _context.Work.AddRange(parent, otherParent, otherChild);
        await _context.SaveChangesAsync();

        await _sut.RestoreChildrenAsync(parent.Id, deletedTime, DeletedBy);
        await _context.SaveChangesAsync();

        (await _context.Work.IgnoreQueryFilters().SingleAsync(w => w.Id == otherChild.Id)).IsDeleted.ShouldBeTrue();
    }

    private const string DeletedBy = "user-1";

    private Work DeletedWork(Guid? parentWorkId, DateTime deletedTime, string? deletedBy) => new()
    {
        Id = Guid.NewGuid(),
        ParentWorkId = parentWorkId,
        ClientId = _sourceClientId,
        CurrentDate = _date,
        IsDeleted = true,
        DeletedTime = deletedTime,
        CurrentUserDeleted = deletedBy
    };

    private Break DeletedBreak(Guid parentWorkId, DateTime deletedTime, string? deletedBy) => new()
    {
        Id = Guid.NewGuid(),
        ParentWorkId = parentWorkId,
        ClientId = _sourceClientId,
        CurrentDate = _date,
        IsDeleted = true,
        DeletedTime = deletedTime,
        CurrentUserDeleted = deletedBy
    };
}
