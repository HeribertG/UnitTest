using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.Groups;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class GroupRepositoryCacheInvalidationTests
{
    private DataBaseContext _context;
    private IGroupCacheService _groupCacheService;
    private GroupRepository _groupRepository;
    private IGroupServiceFacade _mockGroupServiceFacade;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        _groupCacheService = Substitute.For<IGroupCacheService>();

        var mockTreeService = Substitute.For<IGroupTreeService>();
        var mockHierarchyService = Substitute.For<IGroupHierarchyService>();
        var mockSearchService = Substitute.For<IGroupSearchService>();
        var mockValidityService = Substitute.For<IGroupValidityService>();
        var mockMembershipService = Substitute.For<IGroupMembershipService>();
        var mockIntegrityService = Substitute.For<IGroupIntegrityService>();

        mockTreeService.AddRootNodeAsync(Arg.Any<Group>()).Returns(info =>
        {
            var newGroup = info.ArgAt<Group>(0);
            var maxRgt = _context.Group.Max(g => (int?)g.Rgt) ?? 0;
            newGroup.Lft = maxRgt + 1;
            newGroup.Rgt = maxRgt + 2;
            newGroup.Parent = null;
            newGroup.Root = null;
            newGroup.CreateTime = DateTime.UtcNow;

            _context.Group.Add(newGroup);
            return Task.FromResult(newGroup);
        });

        mockTreeService.AddChildNodeAsync(Arg.Any<Guid>(), Arg.Any<Group>()).Returns(info =>
        {
            var parentId = info.ArgAt<Guid>(0);
            var newGroup = info.ArgAt<Group>(1);
            var parent = _context.Group.FirstOrDefault(g => g.Id == parentId);

            if (parent == null)
                throw new KeyNotFoundException($"Parent group with ID {parentId} not found");

            newGroup.Parent = parentId;
            newGroup.Root = parent.Root ?? parent.Id;
            newGroup.Lft = parent.Rgt;
            newGroup.Rgt = parent.Rgt + 1;
            newGroup.CreateTime = DateTime.UtcNow;

            _context.Group.Add(newGroup);
            return Task.FromResult(newGroup);
        });

        mockTreeService.DeleteNodeAsync(Arg.Any<Guid>()).Returns(info =>
        {
            var nodeId = info.ArgAt<Guid>(0);
            var node = _context.Group.FirstOrDefault(g => g.Id == nodeId);

            if (node == null)
                throw new KeyNotFoundException($"Group with ID {nodeId} not found.");

            _context.Group.Remove(node);
            return Task.FromResult(1);
        });

        mockTreeService.MoveNodeAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(info =>
        {
            var nodeId = info.ArgAt<Guid>(0);
            var newParentId = info.ArgAt<Guid>(1);
            var node = _context.Group.FirstOrDefault(g => g.Id == nodeId);
            var newParent = _context.Group.FirstOrDefault(g => g.Id == newParentId);

            if (node == null)
                throw new KeyNotFoundException($"Group to be moved with ID {nodeId} not found");
            if (newParent == null)
                throw new KeyNotFoundException($"New parent group with ID {newParentId} not found");

            node.Parent = newParentId;
            node.Root = newParent.Root ?? newParent.Id;
            _context.Group.Update(node);

            return Task.CompletedTask;
        });

        mockSearchService.ApplyFilters(Arg.Any<IQueryable<Group>>(), Arg.Any<GroupFilter>(), Arg.Any<DateOnly>())
            .Returns(info => info.Arg<IQueryable<Group>>());

        mockMembershipService.UpdateGroupMembershipAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>())
            .Returns(Task.CompletedTask);

        _mockGroupServiceFacade = Substitute.For<IGroupServiceFacade>();
        _mockGroupServiceFacade.TreeService.Returns(mockTreeService);
        _mockGroupServiceFacade.HierarchyService.Returns(mockHierarchyService);
        _mockGroupServiceFacade.SearchService.Returns(mockSearchService);
        _mockGroupServiceFacade.ValidityService.Returns(mockValidityService);
        _mockGroupServiceFacade.MembershipService.Returns(mockMembershipService);
        _mockGroupServiceFacade.IntegrityService.Returns(mockIntegrityService);

        var mockLogger = Substitute.For<ILogger<Group>>();
        _groupRepository = new GroupRepository(
            _context, _mockGroupServiceFacade, _groupCacheService, mockLogger, new FixedCompanyClock(DateTimeOffset.UtcNow));
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task Add_ShouldInvalidateGroupHierarchyCache()
    {
        var newGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "New Root Group",
            Description = "Test group",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(365)
        };

        await _groupRepository.Add(newGroup);

        _groupCacheService.Received(1).InvalidateGroupHierarchyCache();
    }

    [Test]
    public async Task Delete_ShouldInvalidateGroupHierarchyCache()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            Description = "Test",
            ValidFrom = DateTime.Now.AddDays(-100),
            ValidUntil = DateTime.Now.AddDays(100),
            Parent = null,
            Root = null,
            Lft = 1,
            Rgt = 2
        };

        _context.Group.Add(group);
        await _context.SaveChangesAsync();

        await _groupRepository.Delete(group.Id);

        _groupCacheService.Received(1).InvalidateGroupHierarchyCache();
    }

    [Test]
    public async Task MoveNode_ShouldInvalidateGroupHierarchyCache()
    {
        var parentGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Parent Group",
            Description = "Parent",
            ValidFrom = DateTime.Now.AddDays(-100),
            ValidUntil = DateTime.Now.AddDays(100),
            Parent = null,
            Root = null,
            Lft = 1,
            Rgt = 4
        };

        var childGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Child Group",
            Description = "Child",
            ValidFrom = DateTime.Now.AddDays(-50),
            ValidUntil = DateTime.Now.AddDays(50),
            Parent = parentGroup.Id,
            Root = parentGroup.Id,
            Lft = 2,
            Rgt = 3
        };

        var newParentGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "New Parent Group",
            Description = "New Parent",
            ValidFrom = DateTime.Now.AddDays(-80),
            ValidUntil = DateTime.Now.AddDays(80),
            Parent = null,
            Root = null,
            Lft = 5,
            Rgt = 6
        };

        _context.Group.AddRange(parentGroup, childGroup, newParentGroup);
        await _context.SaveChangesAsync();

        await _groupRepository.MoveNode(childGroup.Id, newParentGroup.Id);

        _groupCacheService.Received(1).InvalidateGroupHierarchyCache();
    }

    [Test]
    public async Task Put_ShouldInvalidateGroupHierarchyCache()
    {
        var group = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Test Group",
            Description = "Original description",
            ValidFrom = DateTime.Now.AddDays(-100),
            ValidUntil = DateTime.Now.AddDays(100),
            Parent = null,
            Root = null,
            Lft = 1,
            Rgt = 2
        };

        _context.Group.Add(group);
        await _context.SaveChangesAsync();

        group.Description = "Updated description";

        await _groupRepository.Put(group);

        _groupCacheService.Received(1).InvalidateGroupHierarchyCache();
    }

    [Test]
    public async Task Add_WhenSaveFails_ShouldNotInvalidateCache()
    {
        var mockTreeService = _mockGroupServiceFacade.TreeService;
        mockTreeService.AddRootNodeAsync(Arg.Any<Group>()).Returns<Task<Group>>(x =>
        {
            throw new InvalidOperationException("Database error");
        });

        var newGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "New Group",
            Description = "Test",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(365)
        };

        try
        {
            await _groupRepository.Add(newGroup);
        }
        catch (Exception)
        {
        }

        _groupCacheService.DidNotReceive().InvalidateGroupHierarchyCache();
    }

    [Test]
    public async Task Delete_WhenGroupNotFound_ShouldNotInvalidateCache()
    {
        var nonExistentGroupId = Guid.NewGuid();

        try
        {
            await _groupRepository.Delete(nonExistentGroupId);
        }
        catch (KeyNotFoundException)
        {
        }

        _groupCacheService.DidNotReceive().InvalidateGroupHierarchyCache();
    }
}
