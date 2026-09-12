// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins the empirical answer to the question every "just override it and drop the roles" fix in this
/// codebase depends on: does the [Authorize(Roles = ...)] of InputBaseController.Post survive on an
/// override that carries its own, weaker [Authorize]?
///
/// It does. AuthorizeAttribute is declared [AttributeUsage(..., Inherited = true)], and MVC builds an
/// action's metadata with MethodInfo.GetCustomAttributes(inherit: true) — which walks the base
/// definition chain of an override. Both attributes therefore end up as endpoint metadata, and
/// AuthorizationMiddleware combines all IAuthorizeData of an endpoint into one policy whose
/// requirements are AND-combined. An override can add a restriction; it can never lift one.
///
/// Consequence, and the reason this test exists rather than a comment: a controller whose write verbs
/// must be open to callers without a role cannot inherit from InputBaseController at all — it has to
/// derive from BaseController and declare its own verbs. The tests at the bottom hold the controllers
/// that were converted for exactly that reason (Option B, 2026-09-12) in that state.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.Controllers.UserBackend.Schedules;
using Klacks.Api.Presentation.Controllers.UserBackend.Staffs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using System.Reflection;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class AuthorizeAttributeInheritanceTests
{
    [Test]
    public void AddressesControllerPost_IsAnOverrideOfTheInputBaseAction()
    {
        var post = typeof(AddressesController).GetMethod(nameof(AddressesController.Post), BindingFlags.Public | BindingFlags.Instance);

        post.ShouldNotBeNull();
        post!.DeclaringType.ShouldBe(typeof(AddressesController));
        post.GetBaseDefinition().DeclaringType.ShouldBe(
            typeof(InputBaseController<AddressResource>),
            "Without a real override the inheritance question below would not be asked at all.");
    }

    [Test]
    public void AddressesControllerPost_DeclaresNoAuthorizeAttributeOfItsOwn()
    {
        var post = typeof(AddressesController).GetMethod(nameof(AddressesController.Post), BindingFlags.Public | BindingFlags.Instance);

        post!.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ShouldBeEmpty();
    }

    [Test]
    public void AddressesControllerPost_InheritsTheRoleRestrictionOfTheBaseAction()
    {
        var post = typeof(AddressesController).GetMethod(nameof(AddressesController.Post), BindingFlags.Public | BindingFlags.Instance);

        var inherited = post!.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        inherited.Count.ShouldBe(
            1,
            "GetCustomAttributes(inherit: true) must surface the base action's [Authorize] on the " +
            "override — this is what MVC does when it builds the action model.");
        inherited[0].Roles.ShouldBe($"{Roles.Admin},{Roles.Authorised}");
    }

    [Test]
    public void AnOverrideCannotLiftTheRoleRestriction_ItOnlyAddsASecondAuthorizeAttribute()
    {
        var post = typeof(OverrideProbeController).GetMethod(nameof(OverrideProbeController.Post), BindingFlags.Public | BindingFlags.Instance);

        var own = post!.GetCustomAttributes<AuthorizeAttribute>(inherit: false).ToList();
        var all = post.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        own.Count.ShouldBe(1);
        own[0].Roles.ShouldBeNullOrEmpty();

        all.Count.ShouldBe(
            2,
            "The override's own scheme-only [Authorize] does not replace the base attribute, it joins " +
            "it. AuthorizationMiddleware AND-combines both, so the endpoint still demands the base " +
            "roles — an override is not a way to open an InputBaseController action.");
        all.ShouldContain(a => a.Roles == $"{Roles.Admin},{Roles.Authorised}");
    }

    [Test]
    public void AnnotationsController_DoesNotInheritTheInputBaseRestriction()
    {
        typeof(AnnotationsController).BaseType.ShouldBe(
            typeof(BaseController),
            "Notes must be writable by a Planer, and an override cannot lift the InputBaseController " +
            "role restriction, so the controller declares its own verbs.");

        var post = typeof(AnnotationsController).GetMethod(nameof(AnnotationsController.Post), BindingFlags.Public | BindingFlags.Instance);
        post!.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldAllBe(a => string.IsNullOrEmpty(a.Roles));
    }

    [Test]
    public void BreakPlaceholdersController_DoesNotInheritTheInputBaseRestriction()
    {
        typeof(BreakPlaceholdersController).BaseType.ShouldBe(
            typeof(BaseController),
            "Employee absences in the absence Gantt must be writable by a Planer. While the controller " +
            "inherited from InputBaseController, its scheme-only overrides were AND-combined with the " +
            "base [Authorize(Roles = Admin,Authorised)] and the endpoint stayed closed to them.");

        var post = typeof(BreakPlaceholdersController).GetMethod(nameof(BreakPlaceholdersController.Post), BindingFlags.Public | BindingFlags.Instance);
        post!.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldAllBe(a => string.IsNullOrEmpty(a.Roles));
    }

    [Test]
    public void ClientsController_DoesNotInheritTheInputBaseRestriction()
    {
        typeof(ClientsController).BaseType.ShouldBe(
            typeof(BaseController),
            "The note card of edit-address saves its annotations through the client aggregate, so Put " +
            "must be reachable by a Planer. While the controller inherited from InputBaseController " +
            "that was impossible — the base [Authorize(Roles = Admin,Authorised)] could not be lifted.");

        var put = typeof(ClientsController).GetMethod(nameof(ClientsController.Put), BindingFlags.Public | BindingFlags.Instance);
        put!.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .ShouldAllBe(a => string.IsNullOrEmpty(a.Roles));
    }

    private sealed class OverrideProbeController : InputBaseController<AddressResource>
    {
        public OverrideProbeController()
            : base(null!, null!)
        {
        }

        [HttpPost]
        [Authorize]
        public override Task<ActionResult<AddressResource>> Post([FromBody] AddressResource resource)
            => throw new NotSupportedException();
    }
}
