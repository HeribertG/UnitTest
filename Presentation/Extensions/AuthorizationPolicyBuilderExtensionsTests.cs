// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the assistant gate itself: the policy behind RequireAssistant lets every current role
/// through (the owner decision was to keep the assistant for User too) and, since the Planer floor of
/// Option B (2026-09-12), also an authenticated caller whose token carries no role at all: that caller
/// is a Planer, and the floor includes CanUseAssistant. Skills without a required permission are
/// reachable for them as a consequence — the gate that separates callers is the per-skill permission,
/// not this policy.
///
/// The floor is exactly why authentication has to be checked first. A named policy REPLACES the default
/// policy instead of adding to it, so before the fix of 2026-09-12 an anonymous request reached the
/// assertion with an empty ClaimsPrincipal, whose expansion is the floor — CanUseAssistant included.
///
/// The policy is evaluated through a real IAuthorizationService rather than by iterating the
/// requirements by hand: DenyAnonymousAuthorizationRequirement is served by a framework handler, and a
/// hand-rolled loop over AssertionRequirement alone would report the anonymous caller as allowed and
/// stay green whether the fix is present or not.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Presentation.Extensions;

[TestFixture]
public class AuthorizationPolicyBuilderExtensionsTests
{
    private const string AuthenticationType = "test";

    private static async Task<bool> IsAllowedAsync(ClaimsPrincipal user)
    {
        var policy = new AuthorizationPolicyBuilder().RequireAssistantAccess().Build();

        var provider = new ServiceCollection()
            .AddLogging()
            .AddAuthorization()
            .BuildServiceProvider();

        var authorization = provider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(user, null, policy);

        return result.Succeeded;
    }

    private static ClaimsPrincipal WithRoles(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), AuthenticationType));

    [TestCase(Roles.Admin)]
    [TestCase(Roles.Authorised)]
    [TestCase(Roles.User)]
    public async Task RequireAssistantAccess_EveryRole_IsAllowed(string role)
    {
        (await IsAllowedAsync(WithRoles(role))).ShouldBeTrue();
    }

    [Test]
    public async Task RequireAssistantAccess_AuthenticatedWithoutAnyRole_IsAllowedSinceThePlannerFloorExists()
    {
        (await IsAllowedAsync(WithRoles())).ShouldBeTrue(
            "Option B (2026-09-12) gives an authenticated caller without any role the Planer floor, which " +
            "includes CanUseAssistant. The assistant itself grants nothing — every skill behind it is " +
            "gated by its own requiredPermissions, checked against the same floor.");
    }

    [Test]
    public async Task RequireAssistantAccess_AnonymousPrincipal_IsRefused()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        anonymous.Identity!.IsAuthenticated.ShouldBeFalse(
            "A ClaimsIdentity without an authentication type is the principal AuthorizationMiddleware " +
            "hands to a policy when authentication failed or never ran. If this ever becomes true the " +
            "test below stops testing anonymity.");

        (await IsAllowedAsync(anonymous)).ShouldBeFalse(
            "Permissions.ExpandRoles([]) returns the Planer floor including CanUseAssistant, so the " +
            "assistant assertion alone says yes to a caller who never authenticated. The named policy " +
            "replaces the default policy instead of combining with it, so nothing else asks the question " +
            "— RequireAuthenticatedUser inside RequireAssistantAccess is the only thing refusing here.");
    }
}
