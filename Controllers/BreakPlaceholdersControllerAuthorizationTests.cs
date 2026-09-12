// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The employee absences of the absence Gantt (entity BreakPlaceholder) are the Planer's own write
/// surface, so Post/Put/Delete must be reachable by a caller without any role.
///
/// This fixture used to assert the same thing with GetCustomAttributes(inherit: false) while the
/// controller derived from InputBaseController. That was a green test over an open hole: MVC reads an
/// action's attributes with inherit: true, so the base action's [Authorize(Roles = Admin,Authorised)]
/// was still part of the endpoint metadata and was AND-combined with the scheme-only attribute on the
/// override. The endpoint stayed closed to roleless callers even though this file said otherwise.
/// The assertions below therefore use inherit: true and the controller derives from BaseController.
/// </summary>

using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class BreakPlaceholdersControllerAuthorizationTests
{
    [TestCase("Post")]
    [TestCase("Put")]
    [TestCase("Delete")]
    [TestCase("Get")]
    [TestCase("GetClientList")]
    [TestCase("GetScheduleList")]
    public void EveryEndpoint_IsOpenToAnyAuthenticatedJwtCaller(string methodName)
    {
        var method = typeof(BreakPlaceholdersController)
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);

        method.ShouldNotBeNull($"{methodName} was not found — the test is stale.");

        var attributes = method!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        attributes.Count.ShouldBe(1, $"{methodName} should carry exactly one Authorize attribute");
        attributes[0].AuthenticationSchemes.ShouldBe(
            JwtBearerDefaults.AuthenticationScheme,
            $"{methodName} should use JWT authentication");
        attributes[0].Roles.ShouldBeNullOrEmpty(
            $"{methodName} must not require a role — a Planer records employee absences.");
    }

    [TestCase("Post", typeof(HttpPostAttribute))]
    [TestCase("Put", typeof(HttpPutAttribute))]
    [TestCase("Delete", typeof(HttpDeleteAttribute))]
    [TestCase("Get", typeof(HttpGetAttribute))]
    public void EveryVerb_IsRoutedByItsOwnHttpAttribute(string methodName, Type attributeType)
    {
        var method = typeof(BreakPlaceholdersController)
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);

        method!.GetCustomAttributes(attributeType, inherit: false).ShouldNotBeEmpty();
    }

    [Test]
    public void BreakPlaceholdersController_MustNotInheritFromInputBaseController()
    {
        typeof(BreakPlaceholdersController).BaseType.ShouldBe(
            typeof(BaseController),
            "Deriving from InputBaseController would re-impose its [Authorize(Roles = Admin,Authorised)] " +
            "on Post/Put/Delete through attribute inheritance, which no override can lift.");
    }
}
