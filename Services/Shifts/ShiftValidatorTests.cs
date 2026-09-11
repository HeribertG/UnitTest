using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Application.Mappers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftValidatorTests
{
    private ShiftValidator _validator;
    private DataBaseContext _context;
    private ShiftRepository _shiftRepository;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        var mockLogger = Substitute.For<ILogger<Shift>>();
        var mockQueryPipeline = Substitute.For<IShiftQueryPipelineService>();
        var mockShiftGroupManagementService = Substitute.For<IShiftGroupManagementService>();
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var mockShiftValidator = Substitute.For<IShiftValidator>();

        var scheduleMapper = new ScheduleMapper();
        _shiftRepository = new ShiftRepository(
            _context,
            mockLogger,
            mockQueryPipeline,
            mockShiftGroupManagementService,
            collectionUpdateService,
            mockShiftValidator,
            scheduleMapper,
            new FixedCompanyClock(DateTimeOffset.UtcNow));

        _validator = new ShiftValidator();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task EnsureNoOriginalShiftCopyExists_NoExistingCopy_DoesNotThrowException()
    {
        // Arrange
        var originalShiftId = Guid.NewGuid();

        var originalShift = new Shift
        {
            Id = originalShiftId,
            Name = "Original Shift",
            Status = ShiftStatus.SealedOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        await _context.Shift.AddAsync(originalShift);
        await _context.SaveChangesAsync();

        // Act
        Func<Task> act = async () => await _validator.EnsureNoOriginalShiftCopyExists(originalShiftId, _shiftRepository);

        // Assert
        await act.ShouldNotThrowAsync();
    }

    [Test]
    public async Task EnsureNoOriginalShiftCopyExists_ExistingCopyExists_ThrowsInvalidOperationException()
    {
        // Arrange
        var originalShiftId = Guid.NewGuid();

        var originalShift = new Shift
        {
            Id = originalShiftId,
            Name = "Original Shift",
            Status = ShiftStatus.SealedOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        var existingCopy = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Existing Copy",
            Status = ShiftStatus.OriginalShift,
            OriginalId = originalShiftId,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        await _context.Shift.AddAsync(originalShift);
        await _context.Shift.AddAsync(existingCopy);
        await _context.SaveChangesAsync();

        // Act
        Func<Task> act = async () => await _validator.EnsureNoOriginalShiftCopyExists(originalShiftId, _shiftRepository);

        // Assert
        (await Should.ThrowAsync<InvalidOperationException>(act)).Message.ShouldBe($"An OriginalShift copy already exists for OriginalId={originalShiftId}. Cannot create duplicate copy.");
    }

    [Test]
    public async Task EnsureNoOriginalShiftCopyExists_MultipleSplitShiftsExist_DoesNotThrowException()
    {
        // Arrange
        var originalShiftId = Guid.NewGuid();

        var originalShift = new Shift
        {
            Id = originalShiftId,
            Name = "Original Shift",
            Status = ShiftStatus.SealedOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        var splitShift1 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Split Shift 1",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShiftId,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))
        };

        var splitShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Split Shift 2",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShiftId,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        await _context.Shift.AddAsync(originalShift);
        await _context.Shift.AddAsync(splitShift1);
        await _context.Shift.AddAsync(splitShift2);
        await _context.SaveChangesAsync();

        // Act
        Func<Task> act = async () => await _validator.EnsureNoOriginalShiftCopyExists(originalShiftId, _shiftRepository);

        // Assert
        await act.ShouldNotThrowAsync();
    }

    [Test]
    public async Task EnsureNoOriginalShiftCopyExists_OriginalShiftWithDifferentOriginalId_DoesNotThrowException()
    {
        // Arrange
        var originalShiftId1 = Guid.NewGuid();
        var originalShiftId2 = Guid.NewGuid();

        var originalShift1 = new Shift
        {
            Id = originalShiftId1,
            Name = "Original Shift 1",
            Status = ShiftStatus.SealedOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        var originalShift2 = new Shift
        {
            Id = originalShiftId2,
            Name = "Original Shift 2",
            Status = ShiftStatus.SealedOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        var copyOfShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Copy of Shift 2",
            Status = ShiftStatus.OriginalShift,
            OriginalId = originalShiftId2,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        await _context.Shift.AddAsync(originalShift1);
        await _context.Shift.AddAsync(originalShift2);
        await _context.Shift.AddAsync(copyOfShift2);
        await _context.SaveChangesAsync();

        // Act
        Func<Task> act = async () => await _validator.EnsureNoOriginalShiftCopyExists(originalShiftId1, _shiftRepository);

        // Assert
        await act.ShouldNotThrowAsync();
    }
}
