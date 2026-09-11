// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for EscalationRosterService. GetOrderedRosterAsync (chain construction) is group-scoped: it
/// resolves GroupVisibility for one root, skips a member who is currently absent or has no phone
/// number, and appends the global admin role as a final A2 fallback stage - both stages ordered by
/// AppUser.EscalationRosterOrder. GetRosterMembersAsync/ReorderAsync (admin roster card) are
/// deliberately NOT group-scoped: one flat list of every user with any GroupVisibility and a phone
/// number, unfiltered by absence so the admin can manage an absent member's period. An admin only
/// appears in that flat list if they meet the same visibility+phone criteria themselves - otherwise
/// they remain the invisible A2 fallback used by GetOrderedRosterAsync.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Services.Assistant.Escalation;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Escalation;

[TestFixture]
public class EscalationRosterServiceTests
{
    private static readonly DateTime Today = new(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);

    private string _databaseName = null!;
    private DataBaseContext _dbContext = null!;
    private IGroupRepository _groupRepository = null!;
    private UserManager<AppUser> _userManager = null!;
    private FixedCompanyClock _companyClock = null!;
    private EscalationRosterService _service = null!;

    [Test]
    public async Task GetOrderedRosterAsync_OrdersVisibleMembersByEscalationRosterOrder()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });
        StubNoAdmins();

        var userA = StubUser("user-a", escalationRosterOrder: 3);
        var userB = StubUser("user-b", escalationRosterOrder: 1);
        var userC = StubUser("user-c", escalationRosterOrder: 2);
        AddVisibility(groupId, userA.Id, userB.Id, userC.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(groupId);

        result.Select(c => c.UserId).ShouldBe(new[] { userB.Id, userC.Id, userA.Id });
    }

    [Test]
    public async Task GetOrderedRosterAsync_SkipsMemberWithActiveAbsencePeriod()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });
        StubNoAdmins();

        var absentUser = StubUser("user-absent", escalationRosterOrder: 1);
        var availableUser = StubUser("user-available", escalationRosterOrder: 2);
        AddVisibility(groupId, absentUser.Id, availableUser.Id);
        _dbContext.UserAbsencePeriod.Add(new UserAbsencePeriod
        {
            Id = Guid.NewGuid(),
            AppUserId = absentUser.Id,
            StartDate = DateOnly.FromDateTime(Today).AddDays(-1),
            EndDate = DateOnly.FromDateTime(Today).AddDays(1)
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(groupId);

        result.Select(c => c.UserId).ShouldBe(new[] { availableUser.Id });
    }

    [Test]
    public async Task GetOrderedRosterAsync_SkipsMemberWithNoPhoneNumber()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });
        StubNoAdmins();

        var noPhoneUser = StubUser("user-no-phone", escalationRosterOrder: 1, phoneNumber: null);
        var reachableUser = StubUser("user-reachable", escalationRosterOrder: 2);
        AddVisibility(groupId, noPhoneUser.Id, reachableUser.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(groupId);

        result.Select(c => c.UserId).ShouldBe(new[] { reachableUser.Id });
    }

    [Test]
    public async Task GetOrderedRosterAsync_AppendsAdminNotOtherwiseVisible_AsFinalStage()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        var member = StubUser("user-member", escalationRosterOrder: 1);
        var admin = StubUser("user-admin", escalationRosterOrder: 0);
        AddVisibility(groupId, member.Id);
        _userManager.GetUsersInRoleAsync(Roles.Admin).Returns(new List<AppUser> { admin });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(groupId);

        result.Select(c => c.UserId).ShouldBe(new[] { member.Id, admin.Id });
    }

    [Test]
    public async Task GetOrderedRosterAsync_AdminWithDirectVisibility_AppearsOnceInVisiblePosition()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        var admin = StubUser("user-admin", escalationRosterOrder: 1);
        var member = StubUser("user-member", escalationRosterOrder: 2);
        AddVisibility(groupId, admin.Id, member.Id);
        _userManager.GetUsersInRoleAsync(Roles.Admin).Returns(new List<AppUser> { admin });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(groupId);

        result.Select(c => c.UserId).ShouldBe(new[] { admin.Id, member.Id });
    }

    [Test]
    public async Task GetOrderedRosterAsync_AbsentAdminFallback_IsAlsoSkipped()
    {
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });

        var member = StubUser("user-member", escalationRosterOrder: 1);
        var absentAdmin = StubUser("user-admin", escalationRosterOrder: 0);
        AddVisibility(groupId, member.Id);
        _userManager.GetUsersInRoleAsync(Roles.Admin).Returns(new List<AppUser> { absentAdmin });
        _dbContext.UserAbsencePeriod.Add(new UserAbsencePeriod
        {
            Id = Guid.NewGuid(),
            AppUserId = absentAdmin.Id,
            StartDate = DateOnly.FromDateTime(Today).AddDays(-1),
            EndDate = DateOnly.FromDateTime(Today).AddDays(1)
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(groupId);

        result.Select(c => c.UserId).ShouldBe(new[] { member.Id });
    }

    [Test]
    public async Task GetOrderedRosterAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). An absence period covering only 28.06 must
        // exclude the member under the (correct) company day, even though the (wrong) UTC day 27.06 would
        // not.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        _companyClock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _service = new EscalationRosterService(
            _dbContext, _groupRepository, _userManager, _companyClock, Substitute.For<ILogger<EscalationRosterService>>());
        var groupId = Guid.NewGuid();
        _groupRepository.Get(groupId).Returns(new Group { Id = groupId });
        StubNoAdmins();
        var absentUser = StubUser("user-absent-boundary", escalationRosterOrder: 1);
        var availableUser = StubUser("user-available-boundary", escalationRosterOrder: 2);
        AddVisibility(groupId, absentUser.Id, availableUser.Id);
        _dbContext.UserAbsencePeriod.Add(new UserAbsencePeriod
        {
            Id = Guid.NewGuid(),
            AppUserId = absentUser.Id,
            StartDate = new DateOnly(2026, 6, 28),
            EndDate = new DateOnly(2026, 6, 28)
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(groupId);

        result.Select(c => c.UserId).ShouldBe(new[] { availableUser.Id });
    }

    [Test]
    public async Task GetOrderedRosterAsync_ResolvesChildGroupToRoot()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        _groupRepository.Get(childId).Returns(new Group { Id = childId, Root = rootId });
        StubNoAdmins();

        var member = StubUser("user-member", escalationRosterOrder: 1);
        AddVisibility(rootId, member.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetOrderedRosterAsync(childId);

        result.Select(c => c.UserId).ShouldBe(new[] { member.Id });
    }

    [Test]
    public async Task GetRosterMembersAsync_ReturnsMembersWithAnyVisibilityAndPhone_ExcludingPhoneless()
    {
        var groupId = Guid.NewGuid();

        var absentUser = StubUser("user-absent", escalationRosterOrder: 1);
        var noPhoneUser = StubUser("user-no-phone", escalationRosterOrder: 2, phoneNumber: null);
        var readyUser = StubUser("user-ready", escalationRosterOrder: 3);
        AddVisibility(groupId, absentUser.Id, noPhoneUser.Id, readyUser.Id);
        _dbContext.UserAbsencePeriod.Add(new UserAbsencePeriod
        {
            Id = Guid.NewGuid(),
            AppUserId = absentUser.Id,
            StartDate = DateOnly.FromDateTime(Today).AddDays(-1),
            EndDate = DateOnly.FromDateTime(Today).AddDays(1)
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterMembersAsync();

        result.Select(m => m.UserId).ShouldBe(new[] { absentUser.Id, readyUser.Id }, ignoreOrder: true);
        var absent = result.Single(m => m.UserId == absentUser.Id);
        absent.IsCurrentlyAbsent.ShouldBeTrue();
        var ready = result.Single(m => m.UserId == readyUser.Id);
        ready.IsCurrentlyAbsent.ShouldBeFalse();
    }

    [Test]
    public async Task GetRosterMembersAsync_UnionsVisibilityAcrossAllGroups_NoGroupFilter()
    {
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();

        var memberOfA = StubUser("user-a", escalationRosterOrder: 1);
        var memberOfB = StubUser("user-b", escalationRosterOrder: 2);
        AddVisibility(groupA, memberOfA.Id);
        AddVisibility(groupB, memberOfB.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterMembersAsync();

        result.Select(m => m.UserId).ShouldBe(new[] { memberOfA.Id, memberOfB.Id }, ignoreOrder: true);
    }

    [Test]
    public async Task GetRosterMembersAsync_ExcludesUserWithNoGroupVisibilityAtAll()
    {
        StubUser("user-unaffiliated", escalationRosterOrder: 1);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterMembersAsync();

        result.ShouldBeEmpty();
    }

    [Test]
    public async Task GetRosterMembersAsync_AdminWithoutVisibilityOrPhone_DoesNotAppear()
    {
        var groupId = Guid.NewGuid();
        var member = StubUser("user-member", escalationRosterOrder: 1);
        var admin = StubUser("user-admin", escalationRosterOrder: 2, phoneNumber: null);
        AddVisibility(groupId, member.Id);
        _userManager.GetUsersInRoleAsync(Roles.Admin).Returns(new List<AppUser> { admin });
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterMembersAsync();

        result.Select(m => m.UserId).ShouldBe(new[] { member.Id });
    }

    [Test]
    public async Task GetRosterMembersAsync_OrdersByEscalationRosterOrder()
    {
        var groupId = Guid.NewGuid();

        var userA = StubUser("user-a", escalationRosterOrder: 2);
        var userB = StubUser("user-b", escalationRosterOrder: 1);
        AddVisibility(groupId, userA.Id, userB.Id);
        await _dbContext.SaveChangesAsync();

        var result = await _service.GetRosterMembersAsync();

        result.Select(m => m.UserId).ShouldBe(new[] { userB.Id, userA.Id });
    }

    [Test]
    public async Task ReorderAsync_SetsEscalationRosterOrderToSubmittedPosition()
    {
        var userA = StubUser("user-a", escalationRosterOrder: 5);
        var userB = StubUser("user-b", escalationRosterOrder: 6);
        var userC = StubUser("user-c", escalationRosterOrder: 7);
        await _dbContext.SaveChangesAsync();

        var result = await _service.ReorderAsync(new[] { "user-c", "user-a", "user-b" });

        result.Success.ShouldBeTrue();
        (await _dbContext.AppUser.SingleAsync(u => u.Id == userC.Id)).EscalationRosterOrder.ShouldBe(1);
        (await _dbContext.AppUser.SingleAsync(u => u.Id == userA.Id)).EscalationRosterOrder.ShouldBe(2);
        (await _dbContext.AppUser.SingleAsync(u => u.Id == userB.Id)).EscalationRosterOrder.ShouldBe(3);
    }

    [Test]
    public async Task ReorderAsync_UnknownUserIdInList_IsSilentlyIgnored()
    {
        var userA = StubUser("user-a", escalationRosterOrder: 1);
        await _dbContext.SaveChangesAsync();

        var result = await _service.ReorderAsync(new[] { "user-ghost", "user-a" });

        result.Success.ShouldBeTrue();
        (await _dbContext.AppUser.SingleAsync(u => u.Id == userA.Id)).EscalationRosterOrder.ShouldBe(2);
    }

    [SetUp]
    public void Setup()
    {
        _databaseName = Guid.NewGuid().ToString();
        _dbContext = CreateContext();
        _dbContext.Database.EnsureCreated();

        _groupRepository = Substitute.For<IGroupRepository>();
        _companyClock = new FixedCompanyClock(new DateTimeOffset(Today, TimeSpan.Zero));

        var userStore = Substitute.For<IUserStore<AppUser>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errors = Substitute.For<IdentityErrorDescriber>();
        var services = Substitute.For<IServiceProvider>();
        var userManagerLogger = Substitute.For<ILogger<UserManager<AppUser>>>();

        _userManager = Substitute.For<UserManager<AppUser>>(
            userStore, identityOptions, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errors, services, userManagerLogger);

        var serviceLogger = Substitute.For<ILogger<EscalationRosterService>>();

        _service = new EscalationRosterService(_dbContext, _groupRepository, _userManager, _companyClock, serviceLogger);

        StubNoAdmins();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
        _userManager?.Dispose();
    }

    private DataBaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: _databaseName)
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private void StubNoAdmins()
    {
        _userManager.GetUsersInRoleAsync(Roles.Admin).Returns(new List<AppUser>());
    }

    private void AddVisibility(Guid groupId, params string[] userIds)
    {
        foreach (var userId in userIds)
        {
            _dbContext.GroupVisibility.Add(new GroupVisibility { Id = Guid.NewGuid(), AppUserId = userId, GroupId = groupId });
        }
    }

    private AppUser StubUser(string id, int escalationRosterOrder, string? phoneNumber = "+41 79 000 00 00", string lastName = "Test")
    {
        var user = new AppUser
        {
            Id = id,
            FirstName = id,
            LastName = lastName,
            UserName = $"{id}@test.local",
            EscalationRosterOrder = escalationRosterOrder,
            PhoneNumber = phoneNumber
        };

        _dbContext.AppUser.Add(user);
        return user;
    }
}
