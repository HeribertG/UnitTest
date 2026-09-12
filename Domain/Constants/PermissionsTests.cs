// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies Permissions.HasAllRequiredPermissions: the comma-separated RequiredPermission string
/// stored on AgentSkill must require ALL listed permissions, not an exact-string match against a
/// single user right (regression for the skill-visibility bug where multi-permission skills were
/// unreachable for any non-admin user). Also verifies Permissions.ExpandRoles, the single expansion
/// every assistant entry point now shares.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Constants;
using Shouldly;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class PermissionsTests
{
    [Test]
    public void HasAllRequiredPermissions_NullRequirement_ReturnsTrue()
    {
        Permissions.HasAllRequiredPermissions(new List<string>(), null).ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_EmptyRequirement_ReturnsTrue()
    {
        Permissions.HasAllRequiredPermissions(new List<string>(), string.Empty).ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_SinglePermission_UserHasIt_ReturnsTrue()
    {
        var userRights = new List<string> { Permissions.CanViewGroups };

        Permissions.HasAllRequiredPermissions(userRights, Permissions.CanViewGroups).ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_SinglePermission_UserLacksIt_ReturnsFalse()
    {
        var userRights = new List<string> { Permissions.CanViewClients };

        Permissions.HasAllRequiredPermissions(userRights, Permissions.CanViewGroups).ShouldBeFalse();
    }

    [Test]
    public void HasAllRequiredPermissions_CommaSeparated_UserHasBoth_ReturnsTrue()
    {
        var userRights = new List<string> { Permissions.CanEditClients, Permissions.CanViewGroups };

        Permissions.HasAllRequiredPermissions(userRights, "CanEditClients,CanViewGroups").ShouldBeTrue();
    }

    [Test]
    public void HasAllRequiredPermissions_CommaSeparated_UserHasOnlyOne_ReturnsFalse()
    {
        var userRights = new List<string> { Permissions.CanEditClients };

        Permissions.HasAllRequiredPermissions(userRights, "CanEditClients,CanViewGroups").ShouldBeFalse();
    }

    [Test]
    public void HasAllRequiredPermissions_CommaSeparated_AdminBypasses_ReturnsTrue()
    {
        var userRights = new List<string> { Roles.Admin };

        Permissions.HasAllRequiredPermissions(userRights, "CanEditClients,CanViewGroups").ShouldBeTrue();
    }

    [Test]
    public void ExpandRoles_KeepsTheRoleString_SoTheAdminBypassStillFires()
    {
        var rights = Permissions.ExpandRoles(new[] { Roles.Admin });

        rights.ShouldContain(Roles.Admin);
        Permissions.HasAllRequiredPermissions(rights, "CanEditClients,CanViewGroups").ShouldBeTrue();
    }

    [Test]
    public void ExpandRoles_AddsTheGranularPermissionsOfTheRole()
    {
        var rights = Permissions.ExpandRoles(new[] { Roles.Authorised });

        rights.ShouldContain(Permissions.CanEditClients);
        rights.ShouldContain(Permissions.CanPlan);
        rights.ShouldNotContain(Permissions.CanDeleteClients);
    }

    [Test]
    public void ExpandRoles_MultipleRoles_DoesNotDuplicate()
    {
        var rights = Permissions.ExpandRoles(new[] { Roles.Admin, Roles.Authorised });

        rights.Count(r => r == Permissions.CanEditClients).ShouldBe(1);
        rights.Count(r => r == Permissions.CanViewClients).ShouldBe(1);
    }

    [Test]
    public void ExpandRoles_NoRoles_YieldsThePlannerFloor()
    {
        var rights = Permissions.ExpandRoles(Array.Empty<string>());

        rights.ShouldBe(Permissions.PlannerFloor.ToList(), ignoreOrder: true);
    }

    [Test]
    public void ExpandRoles_NoRoles_CarriesNoRoleName()
    {
        var rights = Permissions.ExpandRoles(Array.Empty<string>());

        rights.ShouldNotContain(Roles.Admin);
        rights.ShouldNotContain(Roles.Authorised);
        rights.ShouldNotContain(Roles.User);
    }

    [Test]
    public void ExpandRoles_BlankRoleNames_AreIgnoredAndStillYieldTheFloor()
    {
        var rights = Permissions.ExpandRoles(new[] { string.Empty, "   " });

        rights.ShouldBe(Permissions.PlannerFloor.ToList(), ignoreOrder: true);
    }

    [Test]
    public void ExpandRoles_NoRoles_MayWriteScheduleWorkAbsencesAndNotes()
    {
        var rights = Permissions.ExpandRoles(Array.Empty<string>());

        rights.ShouldContain(Permissions.CanEditSchedule);
        rights.ShouldContain(Permissions.CanPlan);
        rights.ShouldContain(Permissions.CanEditClientNotes);
        rights.ShouldContain(Permissions.CanUseAssistant);
    }

    [Test]
    public void ExpandRoles_NoRoles_GrantsNoSettingsAndNoDeletes()
    {
        var rights = Permissions.ExpandRoles(Array.Empty<string>());

        rights.ShouldNotContain(Permissions.CanViewSettings);
        rights.ShouldNotContain(Permissions.CanEditSettings);
        rights.ShouldNotContain(Permissions.CanEditClients);
        rights.ShouldNotContain(Permissions.CanDeleteClients);
    }

    [Test]
    public void ExpandRoles_UnknownRole_YieldsTheSameFloorAsNoRoleAtAll()
    {
        var rights = Permissions.ExpandRoles(new[] { "SomeFutureRole" });

        rights.ShouldContain("SomeFutureRole");
        foreach (var permission in Permissions.PlannerFloor)
        {
            rights.ShouldContain(permission);
        }
    }

    [Test]
    public void Authorised_HoldsTheClientNotePermission()
    {
        Permissions.GetPermissionsForRole(Roles.Authorised).ShouldContain(Permissions.CanEditClientNotes);
    }

    [Test]
    public void Admin_HoldsEveryDeclaredPermissionIncludingClientNotes()
    {
        var adminPermissions = Permissions.GetPermissionsForRole(Roles.Admin);
        var declared = typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        declared.ShouldContain(Permissions.CanEditClientNotes);
        foreach (var permission in declared)
        {
            adminPermissions.ShouldContain(permission);
        }
    }

    [Test]
    public void PlannerFloor_IsASubsetOfTheAuthorisedPermissions()
    {
        var authorised = Permissions.GetPermissionsForRole(Roles.Authorised);

        foreach (var permission in Permissions.PlannerFloor)
        {
            authorised.ShouldContain(
                permission,
                "A Supervisor must never hold fewer rights than a role-less Planer — the MCP ceiling " +
                "and the skill gates are both expressed against the Authorised permission set.");
        }
    }

    [TestCase(Roles.Admin)]
    [TestCase(Roles.Authorised)]
    [TestCase(Roles.User)]
    public void GetPermissionsForRole_EveryRole_MayUseTheAssistant(string role)
    {
        Permissions.GetPermissionsForRole(role).ShouldContain(Permissions.CanUseAssistant);
    }

    [Test]
    public void GetPermissionsForRole_UnknownRole_FallsBackToThePlannerFloor()
    {
        var rights = Permissions.GetPermissionsForRole("SomeFutureRole");

        rights.ShouldBe(Permissions.PlannerFloor.ToList(), ignoreOrder: true);
        rights.ShouldContain(Permissions.CanUseAssistant);
        rights.ShouldContain(Permissions.CanViewClients);
        rights.ShouldContain(Permissions.CanEditClientNotes);
        rights.ShouldNotContain(Permissions.CanEditClients);
    }
}
