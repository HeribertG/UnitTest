using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Application.Mappers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Klacks.Api.Domain.Interfaces;
using Klacks.UnitTest.TestHelpers;
using NSubstitute;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ShiftRepositoryTests
{
    private DataBaseContext _context;
    private ShiftRepository _repository;
    private ILogger<Shift> _mockLogger;
    private IHttpContextAccessor _mockHttpContextAccessor;
    private IShiftQueryPipelineService _mockQueryPipeline;
    private IShiftGroupManagementService _mockShiftGroupManagementService;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, _mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<Shift>>();
        _mockQueryPipeline = Substitute.For<IShiftQueryPipelineService>();
        _mockShiftGroupManagementService = Substitute.For<IShiftGroupManagementService>();
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var mockShiftValidator = Substitute.For<IShiftValidator>();
        var scheduleMapper = new ScheduleMapper();
        _repository = new ShiftRepository(
            _context, _mockLogger, _mockQueryPipeline, _mockShiftGroupManagementService, collectionUpdateService,
            mockShiftValidator, scheduleMapper, new FixedCompanyClock(DateTimeOffset.UtcNow));
    }

    [Test]
    public async Task AddOriginalShift_WithStatusIsCutOriginal_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            Lft = 1,
            Rgt = 2
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var cutOriginalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Original Shift",
            Status = ShiftStatus.OriginalShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        // Act
        await _repository.Add(cutOriginalShift);
        await _context.SaveChangesAsync();

        // Assert
        var savedShift = await _context.Shift.FirstOrDefaultAsync(s => s.Id == cutOriginalShift.Id);
        savedShift.ShouldNotBeNull();
        savedShift.OriginalId.ShouldBe(originalShift.Id);
        savedShift.Status.ShouldBe(ShiftStatus.OriginalShift);
    }

    [Test]
    public async Task AddOriginalShift_WithStatusReadyToCut_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            Lft = 1,
            Rgt = 2
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var readyToCutShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Ready to Cut Shift",
            Status = ShiftStatus.SealedOrder,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        // Act
        await _repository.Add(readyToCutShift);
        await _context.SaveChangesAsync();

        // Assert
        var savedShift = await _context.Shift.FirstOrDefaultAsync(s => s.Id == readyToCutShift.Id);
        savedShift.ShouldNotBeNull();
        savedShift.OriginalId.ShouldBe(originalShift.Id);
        savedShift.Status.ShouldBe(ShiftStatus.SealedOrder);
    }

    [Test]
    public async Task AddCutShift_WithStatusIsCut_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var cutShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Shift",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))
        };

        // Act - Direkt speichern ohne Repository Add (wegen InMemory DB Limitation)
        _context.Shift.Add(cutShift);
        await _context.SaveChangesAsync();

        // Assert
        var savedCut = await _context.Shift.FirstOrDefaultAsync(s => s.Id == cutShift.Id);
        
        savedCut.ShouldNotBeNull();
        savedCut.OriginalId.ShouldBe(originalShift.Id);
        savedCut.Status.ShouldBe(ShiftStatus.SplitShift);
    }


    [Test]
    public async Task AddMultipleCutShifts_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var cutShift1 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Shift 1",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))
        };

        var cutShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Shift 2",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        // Act - Direkt speichern ohne Repository Add (wegen InMemory DB Limitation)
        _context.Shift.Add(cutShift1);
        _context.Shift.Add(cutShift2);
        await _context.SaveChangesAsync();

        // Assert
        var allCutShifts = await _context.Shift
            .Where(s => s.OriginalId == originalShift.Id)
            .ToListAsync();

        allCutShifts.Count().ShouldBe(2);
        foreach (var _item in allCutShifts) { _item.OriginalId.ShouldBe(originalShift.Id); };
        foreach (var _item in allCutShifts) { _item.Status.ShouldBe(ShiftStatus.SplitShift); };
    }

    [Test]
    public async Task ShiftWithStatusGreaterThanOne_AlwaysPreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var testCases = new[]
        {
            (ShiftStatus.SealedOrder, "Ready to Cut"),
            (ShiftStatus.OriginalShift, "Is Cut Original"),
            (ShiftStatus.SplitShift, "Is Cut")
        };

        foreach (var (status, name) in testCases)
        {
            // Arrange
            var shift = new Shift
            {
                Id = Guid.NewGuid(),
                Name = $"{name} Shift",
                Status = status,
                OriginalId = originalShift.Id,
                FromDate = DateOnly.FromDateTime(DateTime.Now),
                StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
                EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
            };

            // Act
            if (status == ShiftStatus.SplitShift)
            {
                // Direkt speichern für IsCut wegen InMemory DB Limitation
                _context.Shift.Add(shift);
            }
            else
            {
                await _repository.Add(shift);
            }
            await _context.SaveChangesAsync();

            // Assert
            var savedShift = await _context.Shift.FirstOrDefaultAsync(s => s.Id == shift.Id);
            savedShift.ShouldNotBeNull();
            savedShift.OriginalId.ShouldBe(originalShift.Id, $"OriginalId should be preserved for status {status}");
            savedShift.Status.ShouldBe(status);
        }
    }

    [Test]
    public async Task CopyRequiredQualificationsAsync_CopiesActiveQualificationsToTarget()
    {
        // Arrange
        var sourceShiftId = Guid.NewGuid();
        var targetShiftId = Guid.NewGuid();
        var qualificationA = Guid.NewGuid();
        var qualificationB = Guid.NewGuid();

        _context.ShiftRequiredQualification.AddRange(
            new ShiftRequiredQualification
            {
                Id = Guid.NewGuid(),
                ShiftId = sourceShiftId,
                QualificationId = qualificationA,
                IsMandatory = true,
                MinLevel = QualificationLevel.Advanced,
            },
            new ShiftRequiredQualification
            {
                Id = Guid.NewGuid(),
                ShiftId = sourceShiftId,
                QualificationId = qualificationB,
                IsMandatory = false,
                MinLevel = QualificationLevel.Basic,
            });
        await _context.SaveChangesAsync();

        // Act
        await _repository.CopyRequiredQualificationsAsync(sourceShiftId, targetShiftId);
        await _context.SaveChangesAsync();

        // Assert
        var copied = await _context.ShiftRequiredQualification
            .Where(q => q.ShiftId == targetShiftId)
            .ToListAsync();

        copied.Count.ShouldBe(2);
        copied.ShouldContain(q => q.QualificationId == qualificationA && q.IsMandatory && q.MinLevel == QualificationLevel.Advanced);
        copied.ShouldContain(q => q.QualificationId == qualificationB && !q.IsMandatory && q.MinLevel == QualificationLevel.Basic);

        var sourceStillIntact = await _context.ShiftRequiredQualification
            .Where(q => q.ShiftId == sourceShiftId)
            .CountAsync();
        sourceStillIntact.ShouldBe(2);
    }

    [Test]
    public async Task CopyRequiredQualificationsAsync_WithNoSourceQualifications_AddsNothing()
    {
        // Arrange
        var sourceShiftId = Guid.NewGuid();
        var targetShiftId = Guid.NewGuid();

        // Act
        await _repository.CopyRequiredQualificationsAsync(sourceShiftId, targetShiftId);
        await _context.SaveChangesAsync();

        // Assert
        var copied = await _context.ShiftRequiredQualification
            .Where(q => q.ShiftId == targetShiftId)
            .CountAsync();
        copied.ShouldBe(0);
    }

    [Test]
    public void FilterShifts_WithGroupFilter_IncludesShiftsWithMatchingGroup()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = Guid.NewGuid() }
            }
        };

        _context.Shift.Add(shift);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = groupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockQueryPipeline
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter, DateOnly.FromDateTime(DateTime.UtcNow)).ToList();

        // Assert
        result.Count().ShouldBe(1);
        result.First().Id.ShouldBe(shift.Id);
    }

    [Test]
    public void FilterShifts_WithGroupFilter_IncludesShiftsWithoutGroups()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var shiftWithoutGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift without Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>()
        };

        _context.Shift.Add(shiftWithoutGroup);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = groupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockQueryPipeline
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter, DateOnly.FromDateTime(DateTime.UtcNow)).ToList();

        // Assert
        result.Count().ShouldBe(1);
        result.First().Id.ShouldBe(shiftWithoutGroup.Id);
    }

    [Test]
    public void FilterShifts_WithGroupFilter_ExcludesShiftsWithDifferentGroup()
    {
        // Arrange
        var filterGroupId = Guid.NewGuid();
        var differentGroupId = Guid.NewGuid();
        var shiftWithDifferentGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Different Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = differentGroupId, ShiftId = Guid.NewGuid() }
            }
        };

        _context.Shift.Add(shiftWithDifferentGroup);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = filterGroupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockQueryPipeline
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter, DateOnly.FromDateTime(DateTime.UtcNow)).ToList();

        // Assert
        result.ShouldBeEmpty();
    }

    [Test]
    public void FilterShifts_WithGroupFilter_HandlesMixedShiftsCorrectly()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var differentGroupId = Guid.NewGuid();

        var shiftWithMatchingGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Matching Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = Guid.NewGuid() }
            }
        };

        var shiftWithoutGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift without Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now.AddDays(1)),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>()
        };

        var shiftWithDifferentGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Different Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now.AddDays(2)),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = differentGroupId, ShiftId = Guid.NewGuid() }
            }
        };

        _context.Shift.AddRange(shiftWithMatchingGroup, shiftWithoutGroup, shiftWithDifferentGroup);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = groupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockQueryPipeline
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter, DateOnly.FromDateTime(DateTime.UtcNow)).ToList();

        // Assert
        result.Count().ShouldBe(2);
        result.ShouldContain(s => s.Id == shiftWithMatchingGroup.Id);
        result.ShouldContain(s => s.Id == shiftWithoutGroup.Id);
        result.ShouldNotContain(s => s.Id == shiftWithDifferentGroup.Id);
    }

    [Test]
    public void FilterShifts_UnsealedDraftView_BypassesDateRangeFilter()
    {
        // A far-future FromDate (typical for ERP-imported drafts booked well in advance) must not
        // be hidden by the default "active date range only" filter in the draft review queue.
        var draftFarInFuture = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "ERP draft",
            Status = ShiftStatus.OriginalOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now).AddYears(1),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(7)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(15))
        };
        _context.Shift.Add(draftFarInFuture);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Original,
            IsSealedOrder = false,
            ActiveDateRange = true,
            FormerDateRange = false,
            FutureDateRange = false
        };

        _mockQueryPipeline
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        var result = _repository.FilterShifts(filter, DateOnly.FromDateTime(DateTime.UtcNow)).ToList();

        _mockQueryPipeline.DidNotReceiveWithAnyArgs().ApplyDateRangeFilter(default!, default, default, default, default);
        result.ShouldContain(s => s.Id == draftFarInFuture.Id);
    }

    [Test]
    public void FilterShifts_SealedOrderView_StillAppliesDateRangeFilter()
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Sealed order",
            Status = ShiftStatus.SealedOrder,
            FromDate = DateOnly.FromDateTime(DateTime.Now)
        };
        _context.Shift.Add(shift);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Original,
            IsSealedOrder = true,
            ActiveDateRange = true
        };

        _mockQueryPipeline
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockQueryPipeline
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        _repository.FilterShifts(filter, DateOnly.FromDateTime(DateTime.UtcNow)).ToList();

        _mockQueryPipeline.Received(1).ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), true, false, false, Arg.Any<DateOnly>());
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }
}