using Shouldly;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Groups;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupValidityServiceTests
{
    private DataBaseContext _context;
    private GroupValidityService _validityService;
    private ILogger<GroupValidityService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<GroupValidityService>>();

        _validityService = new GroupValidityService(_context, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithActiveGroups_ShouldReturnActiveGroups()
    {
        // Arrange
        var activeGroupId = Guid.NewGuid();
        var expiredGroupId = Guid.NewGuid();
        var futureGroupId = Guid.NewGuid();

        var now = DateTime.Now;

        var activeGroup = new Group
        {
            Id = activeGroupId,
            Name = "Active Group",
            ValidFrom = now.AddDays(-10),
            ValidUntil = now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var expiredGroup = new Group
        {
            Id = expiredGroupId,
            Name = "Expired Group",
            ValidFrom = now.AddDays(-20),
            ValidUntil = now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        var futureGroup = new Group
        {
            Id = futureGroupId,
            Name = "Future Group",
            ValidFrom = now.AddDays(5),
            ValidUntil = now.AddDays(15),
            Lft = 5,
            Rgt = 6
        };

        await _context.Group.AddRangeAsync(activeGroup, expiredGroup, futureGroup);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, true, false, false, DateOnly.FromDateTime(DateTime.UtcNow));
        var groups = await result.ToListAsync();

        // Assert
        groups.Count().ShouldBe(1);
        groups.ShouldContain(g => g.Id == activeGroupId);
        groups.ShouldNotContain(g => g.Id == expiredGroupId);
        groups.ShouldNotContain(g => g.Id == futureGroupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithFormerGroups_ShouldReturnExpiredGroups()
    {
        // Arrange
        var activeGroupId = Guid.NewGuid();
        var expiredGroupId = Guid.NewGuid();
        var futureGroupId = Guid.NewGuid();

        var now = DateTime.Now;

        var activeGroup = new Group
        {
            Id = activeGroupId,
            Name = "Active Group",
            ValidFrom = now.AddDays(-10),
            ValidUntil = now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var expiredGroup = new Group
        {
            Id = expiredGroupId,
            Name = "Expired Group",
            ValidFrom = now.AddDays(-20),
            ValidUntil = now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        var futureGroup = new Group
        {
            Id = futureGroupId,
            Name = "Future Group",
            ValidFrom = now.AddDays(5),
            ValidUntil = now.AddDays(15),
            Lft = 5,
            Rgt = 6
        };

        await _context.Group.AddRangeAsync(activeGroup, expiredGroup, futureGroup);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, false, true, false, DateOnly.FromDateTime(DateTime.UtcNow));
        var groups = await result.ToListAsync();

        // Assert
        groups.Count().ShouldBe(1);
        groups.ShouldContain(g => g.Id == expiredGroupId);
        groups.ShouldNotContain(g => g.Id == activeGroupId);
        groups.ShouldNotContain(g => g.Id == futureGroupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithFutureGroups_ShouldReturnFutureGroups()
    {
        // Arrange
        var activeGroupId = Guid.NewGuid();
        var expiredGroupId = Guid.NewGuid();
        var futureGroupId = Guid.NewGuid();

        var now = DateTime.Now;

        var activeGroup = new Group
        {
            Id = activeGroupId,
            Name = "Active Group",
            ValidFrom = now.AddDays(-10),
            ValidUntil = now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var expiredGroup = new Group
        {
            Id = expiredGroupId,
            Name = "Expired Group",
            ValidFrom = now.AddDays(-20),
            ValidUntil = now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        var futureGroup = new Group
        {
            Id = futureGroupId,
            Name = "Future Group",
            ValidFrom = now.AddDays(5),
            ValidUntil = now.AddDays(15),
            Lft = 5,
            Rgt = 6
        };

        await _context.Group.AddRangeAsync(activeGroup, expiredGroup, futureGroup);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, false, false, true, DateOnly.FromDateTime(DateTime.UtcNow));
        var groups = await result.ToListAsync();

        // Assert
        groups.Count().ShouldBe(1);
        groups.ShouldContain(g => g.Id == futureGroupId);
        groups.ShouldNotContain(g => g.Id == activeGroupId);
        groups.ShouldNotContain(g => g.Id == expiredGroupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithAllRangesSelected_ShouldReturnAllGroups()
    {
        // Arrange
        var group1Id = Guid.NewGuid();
        var group2Id = Guid.NewGuid();

        var group1 = new Group
        {
            Id = group1Id,
            Name = "Group 1",
            ValidFrom = DateTime.Now.AddDays(-10),
            ValidUntil = DateTime.Now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        var group2 = new Group
        {
            Id = group2Id,
            Name = "Group 2",
            ValidFrom = DateTime.Now.AddDays(-20),
            ValidUntil = DateTime.Now.AddDays(-5),
            Lft = 3,
            Rgt = 4
        };

        await _context.Group.AddRangeAsync(group1, group2);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, true, true, true, DateOnly.FromDateTime(DateTime.UtcNow));
        var groups = await result.ToListAsync();

        // Assert
        groups.Count().ShouldBe(2);
        groups.ShouldContain(g => g.Id == group1Id);
        groups.ShouldContain(g => g.Id == group2Id);
    }

    [Test]
    public async Task ApplyDateRangeFilter_WithNoRangesSelected_ShouldReturnEmptyResult()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var group = new Group
        {
            Id = groupId,
            Name = "Test Group",
            ValidFrom = DateTime.Now.AddDays(-10),
            ValidUntil = DateTime.Now.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        // Act
        var result = _validityService.ApplyDateRangeFilter(baseQuery, false, false, false, DateOnly.FromDateTime(DateTime.UtcNow));

        // The service returns Enumerable.Empty<Group>().AsQueryable() which can't be used with EF async operations
        // So we use the synchronous version
        var groups = result.ToList();

        // Assert
        groups.ShouldBeEmpty();
    }

    [Test]
    public async Task ApplyDateRangeFilter_ValidUntilIsToday_IsActiveNotFormer()
    {
        var groupId = Guid.NewGuid();
        var todayDate = DateTime.UtcNow.Date;
        var today = DateOnly.FromDateTime(todayDate);

        var group = new Group
        {
            Id = groupId,
            Name = "Expires Today",
            ValidFrom = todayDate.AddDays(-10),
            ValidUntil = todayDate,
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        var activeResult = await _validityService.ApplyDateRangeFilter(baseQuery, true, false, false, today).ToListAsync();
        var formerResult = _validityService.ApplyDateRangeFilter(baseQuery, false, true, false, today).ToList();

        activeResult.ShouldContain(g => g.Id == groupId);
        formerResult.ShouldNotContain(g => g.Id == groupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_ValidFromIsToday_IsActiveNotFuture()
    {
        var groupId = Guid.NewGuid();
        var todayDate = DateTime.UtcNow.Date;
        var today = DateOnly.FromDateTime(todayDate);

        var group = new Group
        {
            Id = groupId,
            Name = "Starts Today",
            ValidFrom = todayDate,
            ValidUntil = todayDate.AddDays(10),
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        var activeResult = await _validityService.ApplyDateRangeFilter(baseQuery, true, false, false, today).ToListAsync();
        var futureResult = await _validityService.ApplyDateRangeFilter(baseQuery, false, false, true, today).ToListAsync();

        activeResult.ShouldContain(g => g.Id == groupId);
        futureResult.ShouldNotContain(g => g.Id == groupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_ValidUntilWasYesterday_IsFormer()
    {
        var groupId = Guid.NewGuid();
        var todayDate = DateTime.UtcNow.Date;
        var today = DateOnly.FromDateTime(todayDate);

        var group = new Group
        {
            Id = groupId,
            Name = "Expired Yesterday",
            ValidFrom = todayDate.AddDays(-10),
            ValidUntil = todayDate.AddDays(-1),
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        var formerResult = await _validityService.ApplyDateRangeFilter(baseQuery, false, true, false, today).ToListAsync();
        var activeResult = _validityService.ApplyDateRangeFilter(baseQuery, true, false, false, today).ToList();

        formerResult.ShouldContain(g => g.Id == groupId);
        activeResult.ShouldNotContain(g => g.Id == groupId);
    }

    [Test]
    public async Task ApplyDateRangeFilter_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // Company day (Pacific/Auckland) is 2026-06-28 at the UTC instant 2026-06-27T23:30Z. A group
        // whose ValidFrom is exactly 2026-06-28 must be classified active under the correct company
        // day, but would still be classified future under the (wrong) UTC day 2026-06-27.
        var groupId = Guid.NewGuid();
        var companyDay = new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);
        var group = new Group
        {
            Id = groupId,
            Name = "Auckland Boundary Group",
            ValidFrom = companyDay,
            ValidUntil = companyDay.AddDays(2),
            Lft = 1,
            Rgt = 2
        };

        await _context.Group.AddAsync(group);
        await _context.SaveChangesAsync();

        var baseQuery = _context.Group.AsQueryable();

        var activeUnderCompanyDay = await _validityService
            .ApplyDateRangeFilter(baseQuery, true, false, false, DateOnly.FromDateTime(companyDay))
            .ToListAsync();
        var activeUnderWrongUtcDay = await _validityService
            .ApplyDateRangeFilter(baseQuery, true, false, false, DateOnly.FromDateTime(companyDay.AddDays(-1)))
            .ToListAsync();

        activeUnderCompanyDay.ShouldContain(g => g.Id == groupId);
        activeUnderWrongUtcDay.ShouldNotContain(g => g.Id == groupId);
    }

    [Test]
    public async Task ValidateDateRange_WithValidRange_ShouldReturnTrue()
    {
        // Arrange
        var validFrom = DateTime.Now.AddDays(-5);
        var validUntil = DateTime.Now.AddDays(5);

        // Act
        var testGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Lft = 1,
            Rgt = 2
        };
        var isValid = _validityService.ValidateDateRange(testGroup);

        // Assert
        isValid.ShouldBeTrue();
    }

    [Test]
    public async Task ValidateDateRange_WithInvalidRange_ShouldReturnFalse()
    {
        // Arrange
        var validFrom = DateTime.Now.AddDays(5);
        var validUntil = DateTime.Now.AddDays(-5);

        // Act
        var testGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Lft = 1,
            Rgt = 2
        };
        var isValid = _validityService.ValidateDateRange(testGroup);

        // Assert
        isValid.ShouldBeFalse();
    }
}