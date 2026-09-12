// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Shared test helpers for work handler tests: user identity setup for authorised and regular-user scenarios.
/// </summary>

using Klacks.Api.Domain.Constants;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Klacks.UnitTest.Application.Handlers.Works;

internal static class WorksTestHelpers
{
    public static void GivenUserIsAuthorised(IHttpContextAccessor httpContextAccessor, string userName)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, Roles.Authorised),
                new Claim(ClaimTypes.NameIdentifier, userName)
            ], "TestAuth"));
        httpContextAccessor.HttpContext.Returns(httpContext);
    }

    public static void GivenUserIsAdmin(IHttpContextAccessor httpContextAccessor, string userName)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, Roles.Admin),
                new Claim(ClaimTypes.NameIdentifier, userName)
            ], "TestAuth"));
        httpContextAccessor.HttpContext.Returns(httpContext);
    }

    public static void GivenUserIsRegularUser(IHttpContextAccessor httpContextAccessor, string userName)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, Roles.User),
                new Claim(ClaimTypes.NameIdentifier, userName)
            ], "TestAuth"));
        httpContextAccessor.HttpContext.Returns(httpContext);
    }
}
