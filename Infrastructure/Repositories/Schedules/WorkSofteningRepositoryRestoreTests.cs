// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WorkSofteningRepository.RestoreForClientDayAsync against an in-memory DataBaseContext: a
/// Work delete cascades the client's softenings of that day away with the same delete stamp, so the undo
/// brings back exactly the rows carrying that stamp (same user, same instant within tolerance) and leaves
/// every other deleted softening alone.
/// </summary>

using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class WorkSofteningRepositoryRestoreTests
{
    private const string DeletedBy = "user-1";

    private DataBaseContext _context = null!;
    private WorkSofteningRepository _sut = null!;

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly DateOnly _date = new(2027, 3, 10);
    private readonly DateTime _deletedTime = new(2027, 3, 10, 12, 0, 0, DateTimeKind.Utc);

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _sut = new WorkSofteningRepository(_context);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task RestoreForClientDayAsync_RestoresRowsWithTheSameDeleteStamp()
    {
        var softening = Deleted(_clientId, _date, _deletedTime.AddMilliseconds(3), DeletedBy);
        _context.WorkSoftening.Add(softening);
        await _context.SaveChangesAsync();

        await _sut.RestoreForClientDayAsync(_clientId, _date, null, _deletedTime, DeletedBy, CancellationToken.None);
        await _context.SaveChangesAsync();

        var reloaded = await _context.WorkSoftening.IgnoreQueryFilters().SingleAsync(s => s.Id == softening.Id);
        reloaded.IsDeleted.ShouldBeFalse();
        reloaded.DeletedTime.ShouldBeNull();
        reloaded.CurrentUserDeleted.ShouldBeNull();
    }

    [Test]
    public async Task RestoreForClientDayAsync_LeavesOlderDeletesOfTheSameDayDeleted()
    {
        var older = Deleted(_clientId, _date, _deletedTime.AddMinutes(-5), DeletedBy);
        _context.WorkSoftening.Add(older);
        await _context.SaveChangesAsync();

        await _sut.RestoreForClientDayAsync(_clientId, _date, null, _deletedTime, DeletedBy, CancellationToken.None);
        await _context.SaveChangesAsync();

        var reloaded = await _context.WorkSoftening.IgnoreQueryFilters().SingleAsync(s => s.Id == older.Id);
        reloaded.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task RestoreForClientDayAsync_LeavesRowsDeletedByAnotherActorDeleted()
    {
        var foreign = Deleted(_clientId, _date, _deletedTime, "harmonizer");
        var anonymous = Deleted(_clientId, _date, _deletedTime, null);
        _context.WorkSoftening.AddRange(foreign, anonymous);
        await _context.SaveChangesAsync();

        await _sut.RestoreForClientDayAsync(_clientId, _date, null, _deletedTime, DeletedBy, CancellationToken.None);
        await _context.SaveChangesAsync();

        var rows = await _context.WorkSoftening.IgnoreQueryFilters().ToListAsync();
        rows.ShouldAllBe(r => r.IsDeleted);
    }

    [Test]
    public async Task RestoreForClientDayAsync_IsScopedToClientDayAndScenario()
    {
        var otherClient = Deleted(Guid.NewGuid(), _date, _deletedTime, DeletedBy);
        var otherDay = Deleted(_clientId, _date.AddDays(1), _deletedTime, DeletedBy);
        var scenario = Deleted(_clientId, _date, _deletedTime, DeletedBy);
        scenario.AnalyseToken = Guid.NewGuid();
        _context.WorkSoftening.AddRange(otherClient, otherDay, scenario);
        await _context.SaveChangesAsync();

        await _sut.RestoreForClientDayAsync(_clientId, _date, null, _deletedTime, DeletedBy, CancellationToken.None);
        await _context.SaveChangesAsync();

        var rows = await _context.WorkSoftening.IgnoreQueryFilters().ToListAsync();
        rows.ShouldAllBe(r => r.IsDeleted);
    }

    private static WorkSoftening Deleted(Guid clientId, DateOnly date, DateTime deletedTime, string? deletedBy) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        CurrentDate = date,
        IsDeleted = true,
        DeletedTime = deletedTime,
        CurrentUserDeleted = deletedBy
    };
}
