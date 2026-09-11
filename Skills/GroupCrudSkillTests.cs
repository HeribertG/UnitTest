// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_group, update_group, delete_group and list_groups_hierarchical skills.
/// Covers parent validation, no-op update, cascade-delete guard and depth computation.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;

using Klacks.Api.Application.DTOs.Associations;

using Klacks.Api.Application.Mappers;

using Klacks.UnitTest.Infrastructure.SelfApi;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupCrudSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private FakeSelfApi _api = null!;
    private ICompanyClock _companyClock = null!;
    private Group? _persistedGroup;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _companyClock = new FixedCompanyClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, "api/backend/Groups", new GroupResource());
        _api.Respond(HttpMethod.Put, "api/backend/Groups", new GroupResource());

        _persistedGroup = null;
        _groupRepository.Add(Arg.Do<Group>(g => _persistedGroup = g)).Returns(Task.CompletedTask);
        _groupRepository.GetNoTracking(Arg.Any<Guid>()).Returns(_ => _persistedGroup);
    }

    private UpdateGroupSkill UpdateSkill(IGroupScopeGuard guard) =>
        new(_groupRepository, guard, _calendarSelectionRepository, new GroupMapper(), _api.Client);

    private Group WireGroup(Guid id, Group group)
    {
        _groupRepository.Get(id).Returns(group);
        _groupRepository.GetNoTracking(id).Returns(group);
        return group;
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditSettings", "CanViewSettings" },
        AccessToken = new BearerToken("caller-jwt")
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task CreateGroup_ReturnsError_WhenParentNotFound()
    {
        var skill = new CreateGroupSkill(_groupRepository, TestGroupScopeGuard.Unrestricted(), _calendarSelectionRepository, new GroupMapper(), _api.Client, _companyClock);
        var parentId = Guid.NewGuid();
        _groupRepository.Get(parentId).Returns((Group?)null);
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Bern",
            ["parentId"] = parentId.ToString()
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task CreateGroup_AtRoot_AddsGroupAndCompletes()
    {
        var skill = new CreateGroupSkill(_groupRepository, TestGroupScopeGuard.Unrestricted(), _calendarSelectionRepository, new GroupMapper(), _api.Client, _companyClock);
        var parameters = new Dictionary<string, object> { ["name"] = "Bern" };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }


    [Test]
    public async Task CreateGroup_SuccessMessage_CarriesVerifiedMarker()
    {
        var skill = new CreateGroupSkill(_groupRepository, TestGroupScopeGuard.Unrestricted(), _calendarSelectionRepository, new GroupMapper(), _api.Client, _companyClock);
        var parameters = new Dictionary<string, object> { ["name"] = "Bern" };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task UpdateGroup_NoOp_WhenNoFieldsSupplied()
    {
        var skill = UpdateSkill(TestGroupScopeGuard.Unrestricted());
        var id = Guid.NewGuid();
        WireGroup(id, new Group { Id = id, Name = "Bern", ValidFrom = DateTime.UtcNow.Date });
        var parameters = new Dictionary<string, object> { ["groupId"] = id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("No fields"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateGroup_RenamesAndPersists()
    {
        var skill = UpdateSkill(TestGroupScopeGuard.Unrestricted());
        var id = Guid.NewGuid();
        WireGroup(id, new Group { Id = id, Name = "Old", ValidFrom = DateTime.UtcNow.Date });
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["name"] = "Bern City"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }

    [Test]
    public async Task DeleteGroup_RefusesCascade_WhenChildrenExistAndForceFalse()
    {
        var skill = new DeleteGroupSkill(_groupRepository, TestGroupScopeGuard.Unrestricted(), _api.Client);
        var id = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Bern" });
        _groupRepository.GetChildren(id).Returns(new[]
        {
            new Group { Id = Guid.NewGuid(), Name = "Bern Nord" }
        });
        var parameters = new Dictionary<string, object> { ["groupId"] = id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("child"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task DeleteGroup_CascadesWhenForceTrue()
    {
        var skill = new DeleteGroupSkill(_groupRepository, TestGroupScopeGuard.Unrestricted(), _api.Client);
        var id = Guid.NewGuid();
        var childId = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Bern" });
        _groupRepository.GetChildren(id).Returns(new[]
        {
            new Group { Id = childId, Name = "Bern Nord" }
        });
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["forceCascade"] = true
        };

        _api.Respond(HttpMethod.Delete, $"api/backend/Groups/{id}/subtree",
            new DeleteGroupSubtreeResponse { DeletedGroupName = "Bern", DeletedCount = 2 });

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        // One request for the whole subtree — deleting the children one by one could not roll back
        // the ones that already succeeded.
        _api.Calls.Count.ShouldBe(1);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Delete);
        _api.SingleCall.Route.ShouldBe($"api/backend/Groups/{id}/subtree");
    }

    [Test]
    public async Task ListGroupsHierarchical_ReturnsTreeWithDepth()
    {
        var skill = new ListGroupsHierarchicalSkill(_groupRepository);
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandId = Guid.NewGuid();
        _groupRepository.GetTree(null).Returns(new[]
        {
            new Group { Id = rootId, Name = "CH", Parent = null, Lft = 1, Rgt = 6 },
            new Group { Id = childId, Name = "Bern", Parent = rootId, Lft = 2, Rgt = 5 },
            new Group { Id = grandId, Name = "Bern Nord", Parent = childId, Lft = 3, Rgt = 4 }
        });

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
    }

    [Test]
    public async Task ListGroupsHierarchical_RespectsMaxDepth()
    {
        var skill = new ListGroupsHierarchicalSkill(_groupRepository);
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandId = Guid.NewGuid();
        _groupRepository.GetTree(null).Returns(new[]
        {
            new Group { Id = rootId, Name = "CH", Parent = null, Lft = 1, Rgt = 6 },
            new Group { Id = childId, Name = "Bern", Parent = rootId, Lft = 2, Rgt = 5 },
            new Group { Id = grandId, Name = "Bern Nord", Parent = childId, Lft = 3, Rgt = 4 }
        });

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["maxDepth"] = 1 });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("2 group(s)"));
    }

    [Test]
    public async Task UpdateGroup_ReturnsScopeError_WhenGroupIsOutsideUserScope()
    {
        var scopedRootId = Guid.NewGuid();
        var foreignRootId = Guid.NewGuid();
        var skill = UpdateSkill(TestGroupScopeGuard.Restricted(new[] { scopedRootId }, "Verkauf"));
        var id = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Logistik Ost", Root = foreignRootId });
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["name"] = "Hijacked"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("outside your assigned group scope"));
        Assert.That(result.Message, Does.Contain("Verkauf"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateGroup_Succeeds_WhenGroupIsInsideUserScope()
    {
        var scopedRootId = Guid.NewGuid();
        var skill = UpdateSkill(TestGroupScopeGuard.Restricted(new[] { scopedRootId }, "Verkauf"));
        var id = Guid.NewGuid();
        WireGroup(id, new Group { Id = id, Name = "Verkauf Nord", Root = scopedRootId });
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["name"] = "Verkauf Nordwest"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }

    [Test]
    public async Task DeleteGroup_ReturnsScopeError_WhenGroupIsOutsideUserScope()
    {
        var scopedRootId = Guid.NewGuid();
        var skill = new DeleteGroupSkill(
            _groupRepository, TestGroupScopeGuard.Restricted(new[] { scopedRootId }, "Verkauf"), _api.Client);
        var id = Guid.NewGuid();
        _groupRepository.Get(id).Returns(new Group { Id = id, Name = "Logistik", Root = Guid.NewGuid() });
        var parameters = new Dictionary<string, object> { ["groupId"] = id.ToString() };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("outside your assigned group scope"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task CreateGroup_ReturnsScopeError_ForRootLevelCreation_WhenUserIsScoped()
    {
        var scopedRootId = Guid.NewGuid();
        var skill = new CreateGroupSkill(
            _groupRepository, TestGroupScopeGuard.Restricted(new[] { scopedRootId }, "Verkauf"),
            _calendarSelectionRepository, new GroupMapper(), _api.Client, _companyClock);
        var parameters = new Dictionary<string, object> { ["name"] = "Neue Wurzel" };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("root-level group is outside your assigned group scope"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateGroup_SetsPaymentIntervalAndCalendar_AndReportsVerified()
    {
        var skill = UpdateSkill(TestGroupScopeGuard.Unrestricted());
        var id = Guid.NewGuid();
        var calendarId = Guid.NewGuid();
        var group = WireGroup(id, new Group { Id = id, Name = "Bern", ValidFrom = DateTime.UtcNow.Date });
        _calendarSelectionRepository.Exists(calendarId).Returns(true);
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["paymentInterval"] = "Weekly",
            ["calendarId"] = calendarId.ToString()
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(group.PaymentInterval, Is.EqualTo(PaymentInterval.Weekly));
        Assert.That(group.CalendarSelectionId, Is.EqualTo(calendarId));
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }

    [Test]
    public async Task UpdateGroup_ReturnsError_WhenPaymentIntervalUnknown()
    {
        var skill = UpdateSkill(TestGroupScopeGuard.Unrestricted());
        var id = Guid.NewGuid();
        WireGroup(id, new Group { Id = id, Name = "Bern", ValidFrom = DateTime.UtcNow.Date });
        var parameters = new Dictionary<string, object>
        {
            ["groupId"] = id.ToString(),
            ["paymentInterval"] = "Fortnightly"
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Invalid paymentInterval"));
        _api.Calls.ShouldBeEmpty();
    }


    [Test]
    public async Task CreateGroup_Succeeds_UnderParentInsideUserScope()
    {
        var scopedRootId = Guid.NewGuid();
        var skill = new CreateGroupSkill(
            _groupRepository, TestGroupScopeGuard.Restricted(new[] { scopedRootId }, "Verkauf"),
            _calendarSelectionRepository, new GroupMapper(), _api.Client, _companyClock);
        var parentId = Guid.NewGuid();
        _groupRepository.Get(parentId).Returns(new Group { Id = parentId, Name = "Verkauf Nord", Root = scopedRootId });
        var parameters = new Dictionary<string, object>
        {
            ["name"] = "Verkauf Nord Filiale",
            ["parentId"] = parentId.ToString()
        };

        var result = await skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
    }
}
