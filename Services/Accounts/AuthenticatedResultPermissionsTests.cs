// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Option B spec 2.2: login and refresh hand the UI the exact rights list the backend enforces. The
/// list is produced once, in AccountAuthenticationService.SetAuthenticatedResultAsync, which every
/// token path runs through (login, refresh and the OAuth2 callback via GenerateAuthenticationAsync),
/// so no path can be forgotten. The JWT itself is unchanged — it still carries roles only.
/// The legacy IsAdmin/IsAuthorised flags are asserted alongside the list because all three are now
/// derived from a single role read instead of one user-store round-trip each; a flag that disagrees
/// with the permission list would let the UI show a caller something the API refuses.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AuthenticatedResultPermissionsTests
{
    private ITokenService _tokenService = null!;
    private IAuthenticationService _authenticationService = null!;
    private IUserManagementService _userManagementService = null!;
    private IRefreshTokenService _refreshTokenService = null!;
    private AccountAuthenticationService _service = null!;
    private AppUser _user = null!;

    [SetUp]
    public void SetUp()
    {
        _tokenService = Substitute.For<ITokenService>();
        _authenticationService = Substitute.For<IAuthenticationService>();
        _userManagementService = Substitute.For<IUserManagementService>();
        _refreshTokenService = Substitute.For<IRefreshTokenService>();

        _service = new AccountAuthenticationService(
            _tokenService,
            _authenticationService,
            _userManagementService,
            _refreshTokenService,
            Substitute.For<ILogger<AccountAuthenticationService>>());

        _user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "planner@example.com",
            FirstName = "Pia",
            LastName = "Planner"
        };

        _tokenService.CreateToken(Arg.Any<AppUser>(), Arg.Any<DateTime>()).Returns("token");
    }

    [Test]
    public async Task RolelessUser_ReceivesThePlannerFloor()
    {
        _userManagementService.GetUserRolesAsync(_user).Returns(new List<string>());

        var result = await _service.SetAuthenticatedResultAsync(new AuthenticatedResult(), _user, DateTime.UtcNow);

        result.Permissions.ShouldBe(Permissions.PlannerFloor.ToList(), ignoreOrder: true);
        result.Permissions.ShouldContain(Permissions.CanEditClientNotes);
        result.Permissions.ShouldNotContain(Permissions.CanViewSettings);
        result.IsAdmin.ShouldBeFalse();
        result.IsAuthorised.ShouldBeFalse();
    }

    [Test]
    public async Task AuthorisedUser_ReceivesTheRoleNameAndItsPermissions()
    {
        _userManagementService.GetUserRolesAsync(_user).Returns(new List<string> { Roles.Authorised });

        var result = await _service.SetAuthenticatedResultAsync(new AuthenticatedResult(), _user, DateTime.UtcNow);

        result.Permissions.ShouldContain(Roles.Authorised);
        result.Permissions.ShouldContain(Permissions.CanEditClients);
        result.Permissions.ShouldContain(Permissions.CanEditClientNotes);
        result.Permissions.ShouldNotContain(Permissions.CanDeleteClients);
        result.IsAuthorised.ShouldBeTrue(
            "The legacy flags and the permission list are derived from one role read, so they cannot " +
            "disagree about the caller's role.");
        result.IsAdmin.ShouldBeFalse();
    }

    [Test]
    public async Task AdminUser_ReceivesTheAdminRoleSoTheBypassStillFires()
    {
        _userManagementService.GetUserRolesAsync(_user).Returns(new List<string> { Roles.Admin });

        var result = await _service.SetAuthenticatedResultAsync(new AuthenticatedResult(), _user, DateTime.UtcNow);

        result.Permissions.ShouldContain(Roles.Admin);
        result.Permissions.ShouldContain(Permissions.CanEditSettings);
        Permissions.HasPermission(result.Permissions, Permissions.CanDeleteGroups).ShouldBeTrue();
        result.IsAdmin.ShouldBeTrue();
        result.IsAuthorised.ShouldBeFalse();
    }

    [Test]
    public async Task PermissionsMatchWhatTheRequestSideWouldComputeFromTheRoleClaims()
    {
        _userManagementService.GetUserRolesAsync(_user).Returns(new List<string> { Roles.Authorised });

        var result = await _service.SetAuthenticatedResultAsync(new AuthenticatedResult(), _user, DateTime.UtcNow);

        result.Permissions.ShouldBe(
            Permissions.ExpandRoles(new[] { Roles.Authorised }),
            "The list handed to the UI and the list the API enforces per request must come from the " +
            "same expansion, otherwise the UI hides or offers the wrong things.");
    }
}
