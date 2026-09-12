// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClaimsPrincipal.GetUserRights — the single reader that replaced five copies of the
/// role expansion. It must yield the role string itself (the skill executor's admin bypass matches on
/// it) plus the granular permissions of every role claim. The regression it locks down: the skill API
/// returned role strings only, which left every non-admin failing permission checks.
///
/// Since the planner floor of Option B (2026-09-12) a principal without any role claim no longer yields
/// an empty list but the floor. The reader deliberately never asks whether the principal is
/// authenticated — it only expands claims — so an empty principal produced by a failed authentication
/// also reads as a planner here. Refusing that caller is the job of the authorization policy, checked in
/// AuthorizationPolicyBuilderExtensionsTests.RequireAssistantAccess_AnonymousPrincipal_IsRefused, and of
/// the architecture guard AuthorizationPolicyAnonymousGuardTests.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Extensions;

namespace Klacks.UnitTest.Presentation.Extensions;

[TestFixture]
public class ClaimsPrincipalExtensionsTests
{
    private static ClaimsPrincipal WithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    [Test]
    public void GetUserRights_Authorised_ExpandsToGranularPermissions()
    {
        var rights = WithRoles(Roles.Authorised).GetUserRights();

        rights.ShouldContain(Roles.Authorised);
        rights.ShouldContain(Permissions.CanEditClients);
        rights.ShouldContain(Permissions.CanViewShifts);
    }

    [Test]
    public void GetUserRights_Authorised_PassesAPermissionCheck()
    {
        var rights = WithRoles(Roles.Authorised).GetUserRights();

        Permissions.HasAllRequiredPermissions(rights, Permissions.CanEditClients).ShouldBeTrue();
    }

    [Test]
    public void GetUserRights_Admin_KeepsTheRoleStringForTheBypass()
    {
        var rights = WithRoles(Roles.Admin).GetUserRights();

        rights.ShouldContain(Roles.Admin);
    }

    [Test]
    public void GetUserRights_NoRoleClaims_YieldsThePlannerFloor()
    {
        var rights = WithRoles().GetUserRights();

        rights.ShouldBe(Permissions.ExpandRoles([]), ignoreOrder: true);
        rights.ShouldBe(Permissions.PlannerFloor.ToList(), ignoreOrder: true);
        rights.ShouldNotContain(Permissions.CanViewSettings);
    }

    [Test]
    public void GetUserRights_AnonymousPrincipal_AlsoYieldsTheFloor_WhichIsWhyThePolicyMustDenyAnonymous()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        anonymous.Identity!.IsAuthenticated.ShouldBeFalse();
        anonymous.GetUserRights().ShouldContain(
            Permissions.CanUseAssistant,
            "The reader expands claims and knows nothing about authentication, so an empty principal " +
            "reads as a planner. Nothing here can refuse that caller — only RequireAuthenticatedUser in " +
            "the policy can, which is why RequireAssistantAccess adds it before the assertion.");
    }
}
