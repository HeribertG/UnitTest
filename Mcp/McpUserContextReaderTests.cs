// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Mcp;

namespace Klacks.UnitTest.Mcp;

[TestFixture]
public class McpUserContextReaderTests
{
    [Test]
    public void NullPrincipal_ReturnsAnonymousContext()
    {
        var context = McpUserContextReader.Read(null);

        Assert.That(context.UserId, Is.EqualTo(Guid.Empty));
        Assert.That(context.TenantId, Is.EqualTo(Guid.Empty));
        Assert.That(context.Permissions, Is.Empty);
    }

    [Test]
    public void PrincipalWithClaims_ReturnsParsedIdentity()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var principal = McpTestData.Principal(userId, tenantId, "alice", Roles.Authorised);

        var context = McpUserContextReader.Read(principal);

        Assert.That(context.UserId, Is.EqualTo(userId));
        Assert.That(context.TenantId, Is.EqualTo(tenantId));
        Assert.That(context.UserName, Is.EqualTo("alice"));
    }

    [Test]
    public void AdminPrincipal_PermissionsCappedAtAuthorisedLevel()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "alice", Roles.Admin);

        var context = McpUserContextReader.Read(principal);

        Assert.That(context.Permissions, Is.EquivalentTo(Permissions.GetPermissionsForRole(Roles.Authorised)));
        Assert.That(context.Permissions, Does.Not.Contain(Roles.Admin));
        Assert.That(context.Permissions, Does.Not.Contain(Permissions.CanEditSettings));
        Assert.That(context.Permissions, Does.Not.Contain(Permissions.CanDeleteClients));
    }

    [Test]
    public void AuthorisedPrincipal_KeepsItsOwnPermissionSet()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "bob", Roles.Authorised);

        var context = McpUserContextReader.Read(principal);

        var expected = Permissions.GetPermissionsForRole(Roles.Authorised).Append(Roles.Authorised);
        Assert.That(context.Permissions, Is.EquivalentTo(expected));
    }

    [Test]
    public void PrincipalWithoutRoles_ReturnsThePlannerFloor()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "carol");

        var context = McpUserContextReader.Read(principal);

        Assert.That(context.Permissions, Is.EquivalentTo(Permissions.PlannerFloor));
    }

    [Test]
    public void PrincipalWithoutRoles_StaysBelowTheSupervisorCeiling()
    {
        var principal = McpTestData.Principal(Guid.NewGuid(), Guid.NewGuid(), "carol");

        var context = McpUserContextReader.Read(principal);

        Assert.That(context.Permissions, Does.Not.Contain(Permissions.CanEditSettings));
        Assert.That(context.Permissions, Does.Not.Contain(Permissions.CanDeleteClients));
        Assert.That(context.Permissions, Does.Not.Contain(Roles.Authorised));
    }

    [Test]
    public void PrincipalWithoutGuidClaims_ReturnsEmptyGuids()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "not-a-guid") }, "TestAuth"));

        var context = McpUserContextReader.Read(principal);

        Assert.That(context.UserId, Is.EqualTo(Guid.Empty));
        Assert.That(context.TenantId, Is.EqualTo(Guid.Empty));
    }
}
