// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the restore side of WorkRepository against an in-memory DataBaseContext: GetDeletedAsync
/// sees past the soft-delete query filter but only soft-deleted rows, and RestoreAsync clears the delete
/// stamp on the tracked entity and re-runs the work macro exactly like Add and Put do.
/// </summary>

using Klacks.Api.Domain.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class WorkRepositoryRestoreTests
{
    private DataBaseContext _context = null!;
    private IWorkMacroService _macroService = null!;
    private WorkRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _macroService = Substitute.For<IWorkMacroService>();
        _sut = new WorkRepository(
            _context,
            NullLogger<Work>.Instance,
            Substitute.For<IClientBaseQueryService>(),
            _macroService,
            Substitute.For<IClientContractDataProvider>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task GetDeletedAsync_ReturnsSoftDeletedWork()
    {
        var work = new Work { Id = Guid.NewGuid(), IsDeleted = true, DeletedTime = DateTime.UtcNow, CurrentUserDeleted = "user-1" };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();

        var found = await _sut.GetDeletedAsync(work.Id);

        found.ShouldNotBeNull();
        found!.Id.ShouldBe(work.Id);
    }

    [Test]
    public async Task GetDeletedAsync_ReturnsNull_ForLiveWork()
    {
        var work = new Work { Id = Guid.NewGuid() };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();

        var found = await _sut.GetDeletedAsync(work.Id);

        found.ShouldBeNull();
    }

    [Test]
    public async Task RestoreAsync_ClearsDeleteStamp_AndRunsMacro()
    {
        var work = new Work { Id = Guid.NewGuid(), IsDeleted = true, DeletedTime = DateTime.UtcNow, CurrentUserDeleted = "user-1" };
        _context.Work.Add(work);
        await _context.SaveChangesAsync();
        var tracked = await _sut.GetDeletedAsync(work.Id);

        await _sut.RestoreAsync(tracked!);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Work.SingleAsync(w => w.Id == work.Id);
        reloaded.IsDeleted.ShouldBeFalse();
        reloaded.DeletedTime.ShouldBeNull();
        reloaded.CurrentUserDeleted.ShouldBeNull();
        await _macroService.Received(1).ProcessWorkMacroAsync(tracked!);
    }
}
