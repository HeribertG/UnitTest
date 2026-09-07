// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for WorkRestoreAuthorizer: an Admin sees every deleted Work, the deleting user sees his own
/// delete (CurrentUserDeleted equals his NameIdentifier claim), and everyone else gets Hidden so the
/// endpoint answers exactly like "not found" and reveals nothing about a foreign delete.
/// </summary>

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class WorkRestoreAuthorizerTests
{
    private const string DeletingUser = "user-1";
    private const string OtherUser = "user-2";

    private IHttpContextAccessor _httpContextAccessor = null!;
    private WorkRestoreAuthorizer _sut = null!;

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _sut = new WorkRestoreAuthorizer(_httpContextAccessor);
    }

    [Test]
    public void Resolve_Admin_ReturnsAdmin_EvenForForeignDelete()
    {
        SetupUser(Roles.Admin, OtherUser);

        _sut.Resolve(DeletedBy(DeletingUser)).ShouldBe(WorkRestoreAccess.Admin);
    }

    [Test]
    public void Resolve_DeletingUser_ReturnsOwner()
    {
        SetupUser(Roles.User, DeletingUser);

        _sut.Resolve(DeletedBy(DeletingUser)).ShouldBe(WorkRestoreAccess.Owner);
    }

    [Test]
    public void Resolve_OtherUser_ReturnsHidden()
    {
        SetupUser(Roles.User, OtherUser);

        _sut.Resolve(DeletedBy(DeletingUser)).ShouldBe(WorkRestoreAccess.Hidden);
    }

    [Test]
    public void Resolve_WithoutIdentityClaim_ReturnsHidden()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.User)], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        _sut.Resolve(DeletedBy(DeletingUser)).ShouldBe(WorkRestoreAccess.Hidden);
    }

    [Test]
    public void Resolve_WorkWithoutDeletingUser_ReturnsHidden_ForNonAdmin()
    {
        SetupUser(Roles.User, DeletingUser);

        _sut.Resolve(DeletedBy(null)).ShouldBe(WorkRestoreAccess.Hidden);
    }

    [Test]
    public void Resolve_WithoutHttpContext_ReturnsHidden()
    {
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        _sut.Resolve(DeletedBy(DeletingUser)).ShouldBe(WorkRestoreAccess.Hidden);
    }

    private static Work DeletedBy(string? user) => new() { Id = Guid.NewGuid(), IsDeleted = true, CurrentUserDeleted = user };

    private void SetupUser(string role, string userId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, role),
                new Claim(ClaimTypes.NameIdentifier, userId)
            ], "TestAuth"));
        _httpContextAccessor.HttpContext.Returns(httpContext);
    }
}
