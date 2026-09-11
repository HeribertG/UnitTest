using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Application.Mappers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.ContainerTemplates;

[TestFixture]
public class ContainerAvailableTasksServiceTests
{
    private DataBaseContext _context;
    private ContainerAvailableTasksService _service;
    private ShiftRepository _shiftRepository;
    private GroupItemRepository _groupItemRepository;
    private ContainerTemplateRepository _containerTemplateRepository;
    private ILogger<ContainerAvailableTasksService> _logger;
    private IHttpContextAccessor _mockHttpContextAccessor;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, _mockHttpContextAccessor);

        var shiftLogger = Substitute.For<ILogger<Shift>>();
        var groupItemLogger = Substitute.For<ILogger<GroupItem>>();
        var containerTemplateLogger = Substitute.For<ILogger<ContainerTemplate>>();
        _logger = Substitute.For<ILogger<ContainerAvailableTasksService>>();

        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var mockShiftValidator = Substitute.For<IShiftValidator>();
        var mockQueryPipeline = Substitute.For<IShiftQueryPipelineService>();
        var mockShiftGroupManagementService = Substitute.For<IShiftGroupManagementService>();

        var scheduleMapper = new ScheduleMapper();
        _shiftRepository = new ShiftRepository(
            _context,
            shiftLogger,
            mockQueryPipeline,
            mockShiftGroupManagementService,
            collectionUpdateService,
            mockShiftValidator,
            scheduleMapper,
            new FixedCompanyClock(DateTimeOffset.UtcNow));

        _groupItemRepository = new GroupItemRepository(_context, groupItemLogger);

        var containerTemplateServiceLogger = Substitute.For<ILogger<ContainerTemplateService>>();
        var mockUnitOfWork = Substitute.For<Klacks.Api.Domain.Interfaces.IUnitOfWork>();
        var containerTemplateService = new ContainerTemplateService(mockUnitOfWork, containerTemplateServiceLogger);

        _containerTemplateRepository = new ContainerTemplateRepository(_context, containerTemplateLogger, collectionUpdateService, containerTemplateService);

        _service = new ContainerAvailableTasksService(
            _shiftRepository,
            _groupItemRepository,
            _containerTemplateRepository,
            _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Exclude_Sporadic_Shifts()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var regularShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Regular Shift Monday",
            Abbreviation = "RS1",
            Description = "Regular shift description",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var sporadicShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Sporadic Shift Monday",
            Abbreviation = "SP1",
            Description = "Sporadic shift description",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = true
        };

        var anotherRegularShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Another Regular Shift",
            Abbreviation = "RS2",
            Description = "Another regular shift",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(9, 0),
            EndShift = new TimeOnly(17, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, regularShift, sporadicShift, anotherRegularShift);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var regularShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = regularShift.Id
        };

        var sporadicShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = sporadicShift.Id
        };

        var anotherRegularShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = anotherRegularShift.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            regularShiftGroupItem,
            sporadicShiftGroupItem,
            anotherRegularShiftGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(7, 0),
            untilTime: new TimeOnly(18, 0));

        // Assert
        result.ShouldNotBeNull();
        result.Count().ShouldBe(2);
        result.ShouldNotContain(s => s.IsSporadic);
        result.ShouldContain(s => s.Name == "Regular Shift Monday");
        result.ShouldContain(s => s.Name == "Another Regular Shift");
        result.ShouldNotContain(s => s.Name == "Sporadic Shift Monday");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Exclude_Container_Shifts()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var regularShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Regular Shift",
            Abbreviation = "RS",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var anotherContainerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Another Container",
            Abbreviation = "CNT2",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, regularShift, anotherContainerShift);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var regularShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = regularShift.Id
        };

        var anotherContainerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = anotherContainerShift.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            regularShiftGroupItem,
            anotherContainerGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(7, 0),
            untilTime: new TimeOnly(18, 0));

        // Assert
        result.ShouldNotContain(s => s.ShiftType == ShiftType.IsContainer);
        result.ShouldContain(s => s.Name == "Regular Shift");
        result.ShouldNotContain(s => s.Name == "Another Container");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Only_Return_Shifts_With_Status_OriginalShift_Or_Higher()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var originalOrderShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Order Shift",
            Abbreviation = "OOS",
            Status = ShiftStatus.OriginalOrder,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Abbreviation = "OS",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var splitShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Split Shift",
            Abbreviation = "SS",
            Status = ShiftStatus.SplitShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, originalOrderShift, originalShift, splitShift);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var originalOrderGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = originalOrderShift.Id
        };

        var originalShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = originalShift.Id
        };

        var splitShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = splitShift.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            originalOrderGroupItem,
            originalShiftGroupItem,
            splitShiftGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(7, 0),
            untilTime: new TimeOnly(18, 0));

        // Assert
        result.ShouldNotContain(s => s.Status < ShiftStatus.OriginalShift);
        result.ShouldNotContain(s => s.Name == "Original Order Shift");
        result.ShouldContain(s => s.Name == "Original Shift");
        result.ShouldContain(s => s.Name == "Split Shift");
    }

    [Test]
    public async Task GetAvailableTasksAsync_With_SearchString_Should_Filter_By_Name()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var shiftA = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift Alpha",
            Abbreviation = "SA",
            Description = "Description A",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var shiftB = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift Beta",
            Abbreviation = "SB",
            Description = "Description B",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, shiftA, shiftB);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var shiftAGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftA.Id
        };

        var shiftBGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftB.Id
        };

        await _context.GroupItem.AddRangeAsync(containerGroupItem, shiftAGroupItem, shiftBGroupItem);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(7, 0),
            untilTime: new TimeOnly(18, 0),
            searchString: "Alpha");

        // Assert
        result.Count().ShouldBe(1);
        result.ShouldContain(s => s.Name == "Shift Alpha");
        result.ShouldNotContain(s => s.Name == "Shift Beta");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Exclude_Already_Used_Shifts()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var usedShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Used Shift",
            Abbreviation = "US",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        var availableShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Available Shift",
            Abbreviation = "AS",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, usedShift, availableShift);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var usedShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = usedShift.Id
        };

        var availableShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = availableShift.Id
        };

        await _context.GroupItem.AddRangeAsync(containerGroupItem, usedShiftGroupItem, availableShiftGroupItem);

        var template = new ContainerTemplate
        {
            Id = Guid.NewGuid(),
            ContainerId = containerShift.Id,
            Weekday = 1,
            FromTime = new TimeOnly(8, 0),
            UntilTime = new TimeOnly(16, 0)
        };

        var templateItem = new ContainerTemplateItem
        {
            Id = Guid.NewGuid(),
            ContainerTemplateId = template.Id,
            ShiftId = usedShift.Id
        };

        template.ContainerTemplateItems = new List<ContainerTemplateItem> { templateItem };

        await _context.ContainerTemplate.AddAsync(template);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(7, 0),
            untilTime: new TimeOnly(18, 0));

        // Assert
        result.ShouldNotContain(s => s.Id == usedShift.Id);
        result.ShouldNotContain(s => s.Name == "Used Shift");
        result.ShouldContain(s => s.Name == "Available Shift");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Only_Include_Normal_Shifts_Completely_Within_Timeframe()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var normalShiftInside = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Normal Shift Inside",
            Abbreviation = "NSI",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(9, 0),
            EndShift = new TimeOnly(11, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var normalShiftPartiallyOutside = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Normal Shift Partially Outside",
            Abbreviation = "NSPO",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(7, 0),
            EndShift = new TimeOnly(9, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var normalShiftCompletelyOutside = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Normal Shift Completely Outside",
            Abbreviation = "NSCO",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(5, 0),
            EndShift = new TimeOnly(7, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(
            containerShift,
            normalShiftInside,
            normalShiftPartiallyOutside,
            normalShiftCompletelyOutside);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var normalShiftInsideGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = normalShiftInside.Id
        };

        var normalShiftPartiallyOutsideGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = normalShiftPartiallyOutside.Id
        };

        var normalShiftCompletelyOutsideGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = normalShiftCompletelyOutside.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            normalShiftInsideGroupItem,
            normalShiftPartiallyOutsideGroupItem,
            normalShiftCompletelyOutsideGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(8, 0),
            untilTime: new TimeOnly(12, 0));

        // Assert
        result.ShouldContain(s => s.Name == "Normal Shift Inside");
        result.ShouldNotContain(s => s.Name == "Normal Shift Partially Outside");
        result.ShouldNotContain(s => s.Name == "Normal Shift Completely Outside");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Include_TimeRange_Shifts_With_Overlap()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var timeRangeShiftOverlappingStart = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "TimeRange Shift Overlapping Start",
            Abbreviation = "TRSOS",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(7, 0),
            EndShift = new TimeOnly(9, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        var timeRangeShiftOverlappingEnd = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "TimeRange Shift Overlapping End",
            Abbreviation = "TRSOE",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(11, 0),
            EndShift = new TimeOnly(13, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        var timeRangeShiftInside = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "TimeRange Shift Inside",
            Abbreviation = "TRSI",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(9, 0),
            EndShift = new TimeOnly(11, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        var timeRangeShiftNoOverlap = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "TimeRange Shift No Overlap",
            Abbreviation = "TRSNO",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(5, 0),
            EndShift = new TimeOnly(7, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(
            containerShift,
            timeRangeShiftOverlappingStart,
            timeRangeShiftOverlappingEnd,
            timeRangeShiftInside,
            timeRangeShiftNoOverlap);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var timeRangeShiftOverlappingStartGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = timeRangeShiftOverlappingStart.Id
        };

        var timeRangeShiftOverlappingEndGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = timeRangeShiftOverlappingEnd.Id
        };

        var timeRangeShiftInsideGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = timeRangeShiftInside.Id
        };

        var timeRangeShiftNoOverlapGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = timeRangeShiftNoOverlap.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            timeRangeShiftOverlappingStartGroupItem,
            timeRangeShiftOverlappingEndGroupItem,
            timeRangeShiftInsideGroupItem,
            timeRangeShiftNoOverlapGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(8, 0),
            untilTime: new TimeOnly(12, 0));

        // Assert
        result.ShouldContain(s => s.Name == "TimeRange Shift Overlapping Start");
        result.ShouldContain(s => s.Name == "TimeRange Shift Overlapping End");
        result.ShouldContain(s => s.Name == "TimeRange Shift Inside");
        result.ShouldNotContain(s => s.Name == "TimeRange Shift No Overlap");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Include_Normal_Shifts_At_Boundary()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var shiftEndingAtBoundary = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift Ending At Boundary",
            Abbreviation = "SEAB",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(9, 0),
            EndShift = new TimeOnly(12, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var shiftAfterBoundary = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift After Boundary",
            Abbreviation = "SAB",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(9, 0),
            EndShift = new TimeOnly(13, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, shiftEndingAtBoundary, shiftAfterBoundary);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var shiftEndingAtBoundaryGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftEndingAtBoundary.Id
        };

        var shiftAfterBoundaryGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftAfterBoundary.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            shiftEndingAtBoundaryGroupItem,
            shiftAfterBoundaryGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(8, 0),
            untilTime: new TimeOnly(12, 0));

        // Assert
        result.ShouldContain(s => s.Name == "Shift Ending At Boundary");
        result.ShouldNotContain(s => s.Name == "Shift After Boundary");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Exclude_Normal_Shifts_Crossing_Midnight()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(14, 0),
            EndShift = new TimeOnly(22, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var nightShiftCrossingMidnight = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Night Shift Crossing Midnight",
            Abbreviation = "NSCM",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(23, 0),
            EndShift = new TimeOnly(7, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var regularShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Regular Shift",
            Abbreviation = "RS",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(15, 0),
            EndShift = new TimeOnly(20, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, nightShiftCrossingMidnight, regularShift);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var nightShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = nightShiftCrossingMidnight.Id
        };

        var regularShiftGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = regularShift.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            nightShiftGroupItem,
            regularShiftGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(14, 0),
            untilTime: new TimeOnly(22, 0));

        // Assert
        result.ShouldContain(s => s.Name == "Regular Shift");
        result.ShouldNotContain(s => s.Name == "Night Shift Crossing Midnight");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Handle_Timeframe_Crossing_Midnight_With_Normal_Shifts()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var shiftInLateNight = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift In Late Night",
            Abbreviation = "SILN",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(23, 0),
            EndShift = new TimeOnly(23, 30),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var shiftInEarlyMorning = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift In Early Morning",
            Abbreviation = "SIEM",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(2, 0),
            EndShift = new TimeOnly(5, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        var shiftOutsideTimeframe = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift Outside Timeframe",
            Abbreviation = "SOT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(10, 0),
            EndShift = new TimeOnly(14, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = false
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(containerShift, shiftInLateNight, shiftInEarlyMorning, shiftOutsideTimeframe);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var shiftInLateNightGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftInLateNight.Id
        };

        var shiftInEarlyMorningGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftInEarlyMorning.Id
        };

        var shiftOutsideTimeframeGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = shiftOutsideTimeframe.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            shiftInLateNightGroupItem,
            shiftInEarlyMorningGroupItem,
            shiftOutsideTimeframeGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(22, 0),
            untilTime: new TimeOnly(6, 0));

        // Assert
        result.ShouldContain(s => s.Name == "Shift In Late Night");
        result.ShouldContain(s => s.Name == "Shift In Early Morning");
        result.ShouldNotContain(s => s.Name == "Shift Outside Timeframe");
    }

    [Test]
    public async Task GetAvailableTasksAsync_Should_Handle_Timeframe_Crossing_Midnight_With_TimeRange_Shifts()
    {
        // Arrange
        var today = DateOnly.FromDateTime(DateTime.Now);
        var groupId = Guid.NewGuid();

        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            Description = "Test Group Description"
        };

        var containerShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Container",
            Abbreviation = "CNT",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsContainer,
            FromDate = today,
            StartShift = new TimeOnly(22, 0),
            EndShift = new TimeOnly(6, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        var timeRangeShiftOverlappingStart = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "TimeRange Shift Overlapping Start",
            Abbreviation = "TRSOS",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(20, 0),
            EndShift = new TimeOnly(23, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        var timeRangeShiftOverlappingEnd = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "TimeRange Shift Overlapping End",
            Abbreviation = "TRSOE",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(4, 0),
            EndShift = new TimeOnly(8, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        var timeRangeShiftNoOverlap = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "TimeRange Shift No Overlap",
            Abbreviation = "TRSNO",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = today,
            StartShift = new TimeOnly(10, 0),
            EndShift = new TimeOnly(14, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            IsMonday = true,
            IsSporadic = false,
            IsTimeRange = true
        };

        await _context.Group.AddAsync(group);
        await _context.Shift.AddRangeAsync(
            containerShift,
            timeRangeShiftOverlappingStart,
            timeRangeShiftOverlappingEnd,
            timeRangeShiftNoOverlap);

        var containerGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = containerShift.Id
        };

        var timeRangeShiftOverlappingStartGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = timeRangeShiftOverlappingStart.Id
        };

        var timeRangeShiftOverlappingEndGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = timeRangeShiftOverlappingEnd.Id
        };

        var timeRangeShiftNoOverlapGroupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            ShiftId = timeRangeShiftNoOverlap.Id
        };

        await _context.GroupItem.AddRangeAsync(
            containerGroupItem,
            timeRangeShiftOverlappingStartGroupItem,
            timeRangeShiftOverlappingEndGroupItem,
            timeRangeShiftNoOverlapGroupItem);

        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetAvailableTasksAsync(
            containerShift.Id,
            weekday: 1,
            fromTime: new TimeOnly(22, 0),
            untilTime: new TimeOnly(6, 0));

        // Assert
        result.ShouldContain(s => s.Name == "TimeRange Shift Overlapping Start");
        result.ShouldContain(s => s.Name == "TimeRange Shift Overlapping End");
        result.ShouldNotContain(s => s.Name == "TimeRange Shift No Overlap");
    }
}
