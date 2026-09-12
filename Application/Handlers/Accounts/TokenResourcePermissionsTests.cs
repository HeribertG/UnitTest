// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Option B spec 2.2: the permission list produced by the authentication service must reach the
/// client through TokenResource on both token paths, login and refresh. Without this the UI falls
/// back to IsAdmin/IsAuthorised and a role-less Planer looks right-less to every guard.
/// </summary>

using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs.Registrations;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Accounts;

[TestFixture]
public class TokenResourcePermissionsTests
{
    private const string Email = "planner@example.com";

    private IAccountAuthenticationService _authenticationService = null!;

    [SetUp]
    public void SetUp()
    {
        _authenticationService = Substitute.For<IAccountAuthenticationService>();
    }

    [Test]
    public async Task LoginUserQueryHandler_CopiesThePermissionsIntoTheTokenResource()
    {
        var permissions = Permissions.ExpandRoles(Array.Empty<string>());
        _authenticationService.LogInUserAsync(Email, "password").Returns(Authenticated(permissions));

        var handler = new LoginUserQueryHandler(
            _authenticationService,
            Substitute.For<ILogger<LoginUserQueryHandler>>());

        var result = await handler.Handle(new LoginUserQuery(Email, "password"), CancellationToken.None);

        result.Permissions.ShouldBe(permissions);
        result.Permissions.ShouldContain(Permissions.CanEditClientNotes);
    }

    [Test]
    public async Task RefreshTokenQueryHandler_CopiesThePermissionsIntoTheTokenResource()
    {
        var permissions = Permissions.ExpandRoles(new[] { Roles.Authorised });
        var request = new RefreshRequestResource { Token = "token", RefreshToken = "refresh" };
        _authenticationService.RefreshTokenAsync(request).Returns(Authenticated(permissions));

        var handler = new RefreshTokenQueryHandler(
            _authenticationService,
            Substitute.For<ILogger<RefreshTokenQueryHandler>>());

        var result = await handler.Handle(new RefreshTokenQuery(request), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Permissions.ShouldBe(permissions);
        result.Permissions.ShouldContain(Roles.Authorised);
    }

    private static AuthenticatedResult Authenticated(List<string> permissions) => new()
    {
        Success = true,
        Token = "token",
        RefreshToken = "refresh",
        UserName = Email,
        FirstName = "Pia",
        Name = "Planner",
        Id = Guid.NewGuid().ToString(),
        Expires = DateTime.UtcNow.AddMinutes(15),
        Permissions = permissions
    };
}
