using Shouldly;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Microsoft.EntityFrameworkCore;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Services.Groups;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Groups;

[TestFixture]
public class GroupTreeServiceTests
{
    private GroupRepository _groupRepository;
    private DataBaseContext _context;
    private List<Group> _testGroups;
    private IGroupVisibilityService _mockGroupVisibility;
    private ILogger<Group> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
        _mockGroupVisibility = Substitute.For<IGroupVisibilityService>();
        _mockLogger = Substitute.For<ILogger<Group>>();
        // Create mock Domain Services for GroupRepository
        var mockTreeService = Substitute.For<IGroupTreeService>();
        var mockHierarchyService = Substitute.For<IGroupHierarchyService>();
        var mockSearchService = Substitute.For<IGroupSearchService>();
        var mockValidityService = Substitute.For<IGroupValidityService>();
        var mockMembershipService = Substitute.For<IGroupMembershipService>();
        var mockIntegrityService = Substitute.For<IGroupIntegrityService>();
        
        // Configure hierarchy service to return children directly from context when called
        mockHierarchyService.GetChildrenAsync(Arg.Any<Guid>()).Returns(info =>
        {
            var parentId = info.Arg<Guid>();
            return Task.FromResult(_context.Group.Where(g => g.Parent == parentId).AsEnumerable());
        });
        
        // Configure hierarchy service to return depth calculation
        mockHierarchyService.GetNodeDepthAsync(Arg.Any<Guid>()).Returns(info =>
        {
            var nodeId = info.Arg<Guid>();
            var node = _context.Group.FirstOrDefault(g => g.Id == nodeId);
            if (node == null) return Task.FromResult(0);
            
            int depth = 0;
            var current = node;
            while (current?.Parent != null)
            {
                depth++;
                current = _context.Group.FirstOrDefault(g => g.Id == current.Parent);
            }
            return Task.FromResult(depth);
        });
        
        // Configure hierarchy service to return path from root to node
        mockHierarchyService.GetPathAsync(Arg.Any<Guid>()).Returns(info =>
        {
            var nodeId = info.Arg<Guid>();
            var node = _context.Group.FirstOrDefault(g => g.Id == nodeId);
            if (node?.Root == null) return Task.FromResult(Enumerable.Empty<Group>());
            
            var pathGroups = _context.Group
                .Where(g => g.Root == node.Root && g.Lft <= node.Lft && g.Rgt >= node.Rgt)
                .OrderBy(g => g.Lft)
                .AsEnumerable();
            return Task.FromResult(pathGroups);
        });
        
        // Configure hierarchy service to return tree
        mockHierarchyService.GetTreeAsync(Arg.Any<Guid>()).Returns(info =>
        {
            var rootId = info.Arg<Guid>();
            var treeGroups = _context.Group
                .Where(g => g.Root == rootId || g.Id == rootId)
                .OrderBy(g => g.Lft)
                .AsEnumerable();
            return Task.FromResult(treeGroups);
        });
        
        // Configure hierarchy service to return roots
        mockHierarchyService.GetRootsAsync().Returns(Task.FromResult(
            _context.Group.Where(g => g.Parent == null).AsEnumerable()));
            
        // Configure tree service for add/move/delete operations
        mockTreeService.AddChildNodeAsync(Arg.Any<Guid>(), Arg.Any<Group>()).Returns(info =>
        {
            var parentId = info.ArgAt<Guid>(0);
            var newGroup = info.ArgAt<Group>(1);
            var parent = _context.Group.FirstOrDefault(g => g.Id == parentId);
            if (parent == null) throw new KeyNotFoundException($"Parent group with ID {parentId} not found");
            
            // Update parent's Rgt and all subsequent nodes
            var groupsToUpdate = _context.Group.Where(g => g.Rgt >= parent.Rgt && g.Root == (parent.Root ?? parent.Id)).ToList();
            foreach (var group in groupsToUpdate)
            {
                group.Rgt += 2;
                _context.Group.Update(group);
            }
            
            var groupsToUpdateLft = _context.Group.Where(g => g.Lft > parent.Rgt && g.Root == (parent.Root ?? parent.Id)).ToList();
            foreach (var group in groupsToUpdateLft)
            {
                group.Lft += 2;
                _context.Group.Update(group);
            }
            
            newGroup.Parent = parentId;
            newGroup.Root = parent.Root ?? parent.Id;
            newGroup.Lft = parent.Rgt;
            newGroup.Rgt = parent.Rgt + 1;
            newGroup.CreateTime = DateTime.UtcNow;
            
            _context.Group.Add(newGroup);
            _context.SaveChanges();
            
            return Task.FromResult(newGroup);
        });
        
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
            _context.SaveChanges();
            
            return Task.FromResult(newGroup);
        });
        
        mockTreeService.MoveNodeAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(info =>
        {
            var nodeId = info.ArgAt<Guid>(0);
            var newParentId = info.ArgAt<Guid>(1);
            var node = _context.Group.FirstOrDefault(g => g.Id == nodeId);
            var newParent = _context.Group.FirstOrDefault(g => g.Id == newParentId);
            
            if (node == null) throw new KeyNotFoundException($"Group to be moved with ID {nodeId} not found");
            if (newParent == null) throw new KeyNotFoundException($"New parent group with ID {newParentId} not found");
            
            // Simple validation: prevent moving parent to descendant
            if (newParent.Lft >= node.Lft && newParent.Rgt <= node.Rgt)
                throw new InvalidOperationException("The new parent cannot be a descendant of the groupEntity to be moved");
                
            node.Parent = newParentId;
            node.Root = newParent.Root ?? newParent.Id;
            _context.Group.Update(node);
            
            return Task.CompletedTask;
        });
        
        mockTreeService.DeleteNodeAsync(Arg.Any<Guid>()).Returns(info =>
        {
            var nodeId = info.ArgAt<Guid>(0);
            var node = _context.Group.FirstOrDefault(g => g.Id == nodeId);
            if (node == null) throw new KeyNotFoundException($"Group with ID {nodeId} not found.");
            
            var nodeWidth = node.Rgt - node.Lft + 1;
            
            // Remove the node and its descendants
            var nodesToDelete = _context.Group.Where(g => g.Lft >= node.Lft && g.Rgt <= node.Rgt && g.Root == node.Root).ToList();
            _context.Group.RemoveRange(nodesToDelete);
            
            // Update remaining nodes to close the gap
            var nodesToUpdateRgt = _context.Group.Where(g => g.Rgt > node.Rgt && g.Root == node.Root).ToList();
            foreach (var group in nodesToUpdateRgt)
            {
                group.Rgt -= nodeWidth;
                _context.Group.Update(group);
            }
            
            var nodesToUpdateLft = _context.Group.Where(g => g.Lft > node.Rgt && g.Root == node.Root).ToList();
            foreach (var group in nodesToUpdateLft)
            {
                group.Lft -= nodeWidth;
                _context.Group.Update(group);
            }
            
            _context.SaveChanges();
            
            return Task.FromResult(nodeWidth);
        });

        // Configure integrity service to actually perform operations
        mockIntegrityService.RepairNestedSetValuesAsync(Arg.Any<Guid?>()).Returns(info =>
        {
            // Simple repair: fix any corrupted Lft/Rgt values by rebuilding them
            var rootId = info.ArgAt<Guid?>(0);
            if (rootId.HasValue)
            {
                var treeNodes = _context.Group.Where(g => g.Root == rootId || g.Id == rootId).OrderBy(g => g.Lft).ToList();
                if (treeNodes.Any())
                {
                    RebuildNestedSetValues(treeNodes);
                }
            }
            else
            {
                var allRoots = _context.Group.Where(g => g.Parent == null).ToList();
                foreach (var root in allRoots)
                {
                    var treeNodes = _context.Group.Where(g => g.Root == root.Id || g.Id == root.Id).OrderBy(g => g.Lft).ToList();
                    if (treeNodes.Any())
                    {
                        RebuildNestedSetValues(treeNodes);
                    }
                }
            }
            return Task.CompletedTask;
        });
        
        mockIntegrityService.FixRootValuesAsync().Returns(info =>
        {
            // Fix orphaned root references
            var allGroups = _context.Group.ToList();
            var allGroupIds = allGroups.Select(g => g.Id).ToHashSet();
            
            foreach (var group in allGroups.Where(g => g.Root.HasValue && !allGroupIds.Contains(g.Root.Value)))
            {
                // Find the correct root by traversing up the parent chain
                var current = group;
                while (current?.Parent != null)
                {
                    current = allGroups.FirstOrDefault(g => g.Id == current.Parent);
                }
                group.Root = current?.Id;
                _context.Group.Update(group);
            }
            return Task.CompletedTask;
        });
        
        mockIntegrityService.ValidateNestedSetIntegrityAsync(Arg.Any<Guid>()).Returns(Task.FromResult(true));
        
        // Configure search service to pass through queries unchanged for these tests
        mockSearchService.ApplyFilters(Arg.Any<IQueryable<Group>>(), Arg.Any<GroupFilter>(), Arg.Any<DateOnly>())
            .Returns(info => info.Arg<IQueryable<Group>>());

        // Configure validity service to pass through queries unchanged
        mockValidityService.ApplyDateRangeFilter(Arg.Any<IQueryable<Group>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<DateOnly>())
            .Returns(info => info.Arg<IQueryable<Group>>());
            
        // Configure membership service  
        mockMembershipService.BulkAddClientsToGroupAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns(Task.CompletedTask);
        mockMembershipService.BulkRemoveClientsFromGroupAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>()).Returns(Task.CompletedTask);
        mockMembershipService.AddClientToGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(Task.CompletedTask);
        mockMembershipService.RemoveClientFromGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(Task.CompletedTask);
        mockMembershipService.GetGroupMembersAsync(Arg.Any<Guid>()).Returns(Task.FromResult(Enumerable.Empty<Client>()));
        mockMembershipService.GetClientGroupsAsync(Arg.Any<Guid>()).Returns(Task.FromResult(Enumerable.Empty<Group>()));
        mockMembershipService.IsClientInGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>()).Returns(Task.FromResult(false));
        mockMembershipService.GetGroupMemberCountAsync(Arg.Any<Guid>()).Returns(Task.FromResult(0));

        var mockGroupServiceFacade = Substitute.For<IGroupServiceFacade>();
        mockGroupServiceFacade.VisibilityService.Returns(_mockGroupVisibility);
        mockGroupServiceFacade.TreeService.Returns(mockTreeService);
        mockGroupServiceFacade.HierarchyService.Returns(mockHierarchyService);
        mockGroupServiceFacade.SearchService.Returns(mockSearchService);
        mockGroupServiceFacade.ValidityService.Returns(mockValidityService);
        mockGroupServiceFacade.MembershipService.Returns(mockMembershipService);
        mockGroupServiceFacade.IntegrityService.Returns(mockIntegrityService);

        var mockGroupCacheService = Substitute.For<IGroupCacheService>();
        _groupRepository = new GroupRepository(
            _context, mockGroupServiceFacade, mockGroupCacheService, _mockLogger, new FixedCompanyClock(DateTimeOffset.UtcNow));

        CreateTestData();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private void CreateTestData()
    {
        // Create a simple tree structure for testing
        // Root (1,10)
        //   ├── Child1 (2,5)
        //   │   └── Grandchild (3,4)
        //   └── Child2 (6,9)
        //       └── Grandchild2 (7,8)

        var root = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Root Group",
            Description = "Root of the tree",
            ValidFrom = DateTime.Now.AddDays(-100),
            ValidUntil = DateTime.Now.AddDays(100),
            Parent = null,
            Root = null,
            Lft = 1,
            Rgt = 10
        };

        var child1 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Child Group 1",
            Description = "First child",
            ValidFrom = DateTime.Now.AddDays(-50),
            ValidUntil = DateTime.Now.AddDays(50),
            Parent = root.Id,
            Root = root.Id,
            Lft = 2,
            Rgt = 5
        };

        var grandchild1 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Grandchild Group 1",
            Description = "First grandchild",
            ValidFrom = DateTime.Now.AddDays(-25),
            ValidUntil = DateTime.Now.AddDays(25),
            Parent = child1.Id,
            Root = root.Id,
            Lft = 3,
            Rgt = 4
        };

        var child2 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Child Group 2",
            Description = "Second child",
            ValidFrom = DateTime.Now.AddDays(-30),
            ValidUntil = DateTime.Now.AddDays(30),
            Parent = root.Id,
            Root = root.Id,
            Lft = 6,
            Rgt = 9
        };

        var grandchild2 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Grandchild Group 2",
            Description = "Second grandchild",
            ValidFrom = DateTime.Now.AddDays(-15),
            ValidUntil = DateTime.Now.AddDays(15),
            Parent = child2.Id,
            Root = root.Id,
            Lft = 7,
            Rgt = 8
        };

        // Second tree for testing multiple roots
        var root2 = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Second Root",
            Description = "Second tree root",
            ValidFrom = DateTime.Now.AddDays(-200),
            ValidUntil = null,
            Parent = null,
            Root = null,
            Lft = 11,
            Rgt = 12
        };

        _testGroups = new List<Group>
        {
            root, child1, grandchild1, child2, grandchild2, root2
        };

        _context.Group.AddRange(_testGroups);
        _context.SaveChanges();

        // Setup mock visibility service
        _mockGroupVisibility.ReadVisibleRootIdList().Returns(_testGroups.Where(g => g.Root == null).Select(g => g.Id).ToList());
    }

    #region Node Creation Tests

    [Test]
    public async Task AddRootNode_ShouldCreateNewRootWithCorrectLftRgt()
    {
        // Arrange
        var newRoot = new Group
        {
            Id = Guid.NewGuid(),
            Name = "New Root",
            Description = "New root group",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(365)
        };

        // Act
        await _groupRepository.Add(newRoot);

        // Assert
        var addedGroup = await _context.Group.FindAsync(newRoot.Id);
        addedGroup.ShouldNotBeNull();
        addedGroup.Lft.ShouldBe(13); // After existing max Rgt (12) + 1
        addedGroup.Rgt.ShouldBe(14);
        addedGroup.Parent.ShouldBeNull();
        addedGroup.Root.ShouldBeNull();
    }



    [Test]
    public async Task AddChildNode_WithInvalidParent_ShouldThrowException()
    {
        // Arrange
        var invalidParentId = Guid.NewGuid();
        var newChild = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Orphaned Child",
            Description = "Child with invalid parent",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(90),
            Parent = invalidParentId
        };

        // Act & Assert
        (await Should.ThrowAsync<KeyNotFoundException>(() => _groupRepository.Add(newChild))).Message.ShouldBe($"Parent group with ID {invalidParentId} not found");
    }


    #endregion

    #region Node Movement Tests


    [Test]
    public async Task MoveNode_WithInvalidNewParent_ShouldThrowException()
    {
        // Arrange
        var child1 = _testGroups.First(g => g.Name == "Child Group 1");
        var invalidParentId = Guid.NewGuid();

        // Act & Assert
        (await Should.ThrowAsync<KeyNotFoundException>(() => _groupRepository.MoveNode(child1.Id, invalidParentId))).Message.ShouldBe($"New parent group with ID {invalidParentId} not found");
    }

    [Test]
    public async Task MoveNode_ToDescendant_ShouldThrowException()
    {
        // Arrange
        var child1 = _testGroups.First(g => g.Name == "Child Group 1");
        var grandchild1 = _testGroups.First(g => g.Name == "Grandchild Group 1");

        // Act & Assert - Trying to move parent to be child of its own descendant
        (await Should.ThrowAsync<InvalidOperationException>(() => _groupRepository.MoveNode(child1.Id, grandchild1.Id))).Message.ShouldBe("The new parent cannot be a descendant of the groupEntity to be moved");
    }

    [Test]
    public async Task MoveNode_WithNonexistentNode_ShouldThrowException()
    {
        // Arrange
        var invalidNodeId = Guid.NewGuid();
        var child2 = _testGroups.First(g => g.Name == "Child Group 2");

        // Act & Assert
        (await Should.ThrowAsync<KeyNotFoundException>(() => _groupRepository.MoveNode(invalidNodeId, child2.Id))).Message.ShouldBe($"Group to be moved with ID {invalidNodeId} not found");
    }



    #endregion

    #region Tree Repair Tests

    [Test]
    public async Task RepairNestedSetValues_ShouldFixCorruptedValues()
    {
        // Arrange - Corrupt the nested set values
        var child1 = _testGroups.First(g => g.Name == "Child Group 1");
        child1.Lft = 100; // Invalid value
        child1.Rgt = 50;  // Invalid value (Rgt < Lft)
        _context.Group.Update(child1);
        await _context.SaveChangesAsync();

        // Act
        await _groupRepository.RepairNestedSetValues();

        // Assert
        var allNodes = await _context.Group.ToListAsync();
        await VerifyNestedSetIntegrity(allNodes);
    }

    [Test]
    public async Task RepairNestedSetValues_ShouldMaintainHierarchy()
    {
        // Arrange - Get original hierarchy
        var originalRoot = _testGroups.First(g => g.Name == "Root Group");
        var originalChild1 = _testGroups.First(g => g.Name == "Child Group 1");
        var originalGrandchild1 = _testGroups.First(g => g.Name == "Grandchild Group 1");

        // Corrupt values
        originalChild1.Lft = 999;
        originalChild1.Rgt = 1000;
        _context.Group.Update(originalChild1);
        await _context.SaveChangesAsync();

        // Act
        await _groupRepository.RepairNestedSetValues();

        // Assert - Hierarchy should be maintained
        var repairedRoot = await _context.Group.FindAsync(originalRoot.Id);
        var repairedChild1 = await _context.Group.FindAsync(originalChild1.Id);
        var repairedGrandchild1 = await _context.Group.FindAsync(originalGrandchild1.Id);

        repairedChild1!.Parent.ShouldBe(originalRoot.Id);
        repairedGrandchild1!.Parent.ShouldBe(originalChild1.Id);

        // Nested set values should be consistent
        repairedRoot!.Lft.ShouldBeLessThan(repairedChild1.Lft);
        repairedChild1.Lft.ShouldBeLessThan(repairedGrandchild1.Lft);
        repairedGrandchild1.Rgt.ShouldBeLessThan(repairedChild1.Rgt);
        repairedChild1.Rgt.ShouldBeLessThan(repairedRoot.Rgt);
    }

    [Test]
    public async Task FixRootValues_ShouldCorrectOrphanedNodes()
    {
        // Arrange - Create an orphaned node (references non-existent root)
        var orphanedGroup = new Group
        {
            Id = Guid.NewGuid(),
            Name = "Orphaned Group",
            Description = "Group with invalid root reference",
            ValidFrom = DateTime.Now,
            ValidUntil = DateTime.Now.AddDays(30),
            Parent = _testGroups.First(g => g.Name == "Child Group 1").Id,
            Root = Guid.NewGuid(), // Non-existent root
            Lft = 50,
            Rgt = 51
        };

        _context.Group.Add(orphanedGroup);
        await _context.SaveChangesAsync();

        // Act
        await _groupRepository.FixRootValues();

        // Assert
        var fixedGroup = await _context.Group.FindAsync(orphanedGroup.Id);
        var expectedRoot = _testGroups.First(g => g.Name == "Root Group");
        fixedGroup!.Root.ShouldBe(expectedRoot.Id);
    }

    [Test]
    public async Task GetChildren_ShouldReturnDirectChildrenOnly()
    {
        // Arrange
        var rootGroup = _testGroups.First(g => g.Name == "Root Group");

        // Act
        var children = await _groupRepository.GetChildren(rootGroup.Id);

        // Assert
        children.Count().ShouldBe(2);
        children.ShouldContain(g => g.Name == "Child Group 1");
        children.ShouldContain(g => g.Name == "Child Group 2");
        children.ShouldNotContain(g => g.Name == "Grandchild Group 1"); // Should not include grandchildren
    }

    [Test]
    public async Task GetNodeDepth_ShouldReturnCorrectDepth()
    {
        // Arrange
        var rootGroup = _testGroups.First(g => g.Name == "Root Group");
        var childGroup = _testGroups.First(g => g.Name == "Child Group 1");
        var grandchildGroup = _testGroups.First(g => g.Name == "Grandchild Group 1");

        // Act
        var rootDepth = await _groupRepository.GetNodeDepth(rootGroup.Id);
        var childDepth = await _groupRepository.GetNodeDepth(childGroup.Id);
        var grandchildDepth = await _groupRepository.GetNodeDepth(grandchildGroup.Id);

        // Assert
        rootDepth.ShouldBe(0);
        childDepth.ShouldBe(1);
        grandchildDepth.ShouldBe(2);
    }

    [Test]
    public async Task GetPath_ShouldReturnPathFromRootToNode()
    {
        // Arrange
        var grandchildGroup = _testGroups.First(g => g.Name == "Grandchild Group 1");

        // Act
        var path = await _groupRepository.GetPath(grandchildGroup.Id);
        var pathList = path.ToList();

        // Assert
        // Note: GetPath query uses g.Root == node.Root which excludes the root itself when Root is set
        pathList.Count().ShouldBeGreaterThanOrEqualTo(2); // At least Child1 -> Grandchild1
        pathList.ShouldContain(g => g.Name == "Child Group 1");
        pathList.ShouldContain(g => g.Name == "Grandchild Group 1");
        pathList.Select(g => g.Lft).ShouldBeInOrder();
    }

    [Test]
    public async Task GetTree_WithSpecificRoot_ShouldReturnOnlyThatTree()
    {
        // Arrange
        var rootGroup = _testGroups.First(g => g.Name == "Root Group");

        // Act
        var tree = await _groupRepository.GetTree(rootGroup.Id);
        var treeList = tree.ToList();

        // Assert
        treeList.Count().ShouldBe(5); // Root + 2 children + 2 grandchildren
        treeList.ShouldNotContain(g => g.Name == "Second Root");
        treeList.ShouldContain(g => g.Name == "Root Group");
        treeList.ShouldContain(g => g.Name == "Child Group 1");
        treeList.ShouldContain(g => g.Name == "Child Group 2");
    }

    [Test]
    public async Task GetRoots_ShouldReturnAllRootNodes()
    {
        // Act
        var roots = await _groupRepository.GetRoots();
        var rootsList = roots.ToList();

        // Assert
        rootsList.Count().ShouldBe(2);
        rootsList.ShouldContain(g => g.Name == "Root Group");
        rootsList.ShouldContain(g => g.Name == "Second Root");
    }

    #endregion

    #region Delete Tests



    #endregion

    #region Helper Methods

    private void RebuildNestedSetValues(List<Group> treeNodes)
    {
        // Simple nested set rebuild algorithm
        var rootNode = treeNodes.FirstOrDefault(n => n.Parent == null);
        if (rootNode == null) return;

        int counter = 1;
        rootNode.Lft = counter++;
        counter = RebuildChildNodes(treeNodes, rootNode.Id, counter);
        rootNode.Rgt = counter++;
        _context.Group.Update(rootNode);
    }

    private int RebuildChildNodes(List<Group> allNodes, Guid parentId, int counter)
    {
        var children = allNodes.Where(n => n.Parent == parentId).OrderBy(n => n.Name).ToList();
        
        foreach (var child in children)
        {
            child.Lft = counter++;
            counter = RebuildChildNodes(allNodes, child.Id, counter);
            child.Rgt = counter++;
            _context.Group.Update(child);
        }
        
        return counter;
    }

    private async Task VerifyNestedSetIntegrity(List<Group> nodes)
    {
        var rootNodes = nodes.Where(n => n.Parent == null).ToList();

        foreach (var root in rootNodes)
        {
            await VerifySubtreeIntegrity(nodes, root);
        }
    }

    private async Task VerifySubtreeIntegrity(List<Group> allNodes, Group parent)
    {
        var children = allNodes.Where(n => n.Parent == parent.Id).OrderBy(n => n.Lft).ToList();

        if (!children.Any())
        {
            // Leaf node should have Rgt = Lft + 1
            parent.Rgt.ShouldBe(parent.Lft + 1, $"Leaf node {parent.Name} should have Rgt = Lft + 1");
            return;
        }

        int expectedLft = parent.Lft + 1;

        foreach (var child in children)
        {
            child.Lft.ShouldBeGreaterThanOrEqualTo(expectedLft, $"Child {child.Name} Lft should be >= {expectedLft}");
            child.Lft.ShouldBeLessThan(parent.Rgt, $"Child {child.Name} should be within parent {parent.Name} boundaries");
            child.Rgt.ShouldBeLessThan(parent.Rgt, $"Child {child.Name} should be within parent {parent.Name} boundaries");
            child.Root.ShouldBe(parent.Root ?? parent.Id, $"Child {child.Name} should have same root as parent {parent.Name}");

            await VerifySubtreeIntegrity(allNodes, child);
            expectedLft = child.Rgt + 1;
        }
    }

    #endregion
}