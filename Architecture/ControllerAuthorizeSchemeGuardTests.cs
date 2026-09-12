// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Closes the gap that .claude/rules/code-policies.md marked open: there was a reflection guard for
/// SignalR hubs (Infrastructure/Hubs/HubAuthorizationTests.cs) and a source-scan guard for
/// scheme-less Forbid()/Challenge() (ForbidChallengeSchemeGuardTests.cs), but none for [Authorize]
/// on controllers.
///
/// The trap: Program.cs registers JwtBearer first, then AddIdentity moves the DEFAULT authentication
/// scheme to cookie auth. An [Authorize] that names no scheme resolves that default, so a JWT caller
/// is answered with a redirect to a login page instead of being authorised — every request 401s.
///
/// What "pinned" means here, and why it is a per-type rule rather than a per-attribute one:
/// AuthorizationMiddleware combines ALL IAuthorizeData of an endpoint (class-level and method-level,
/// including the ones inherited from base controllers) into a single policy whose AuthenticationSchemes
/// are the union of all of them. One class-level [Authorize(AuthenticationSchemes = Jwt...)] anywhere
/// in a controller's inheritance chain therefore supplies the scheme for every action of that
/// controller — which is exactly how BaseController works and why method-level [Authorize(Roles = ...)]
/// on its descendants is correct without repeating the scheme. A controller with no such ancestor must
/// pin the scheme on each of its own [Authorize] attributes.
///
/// Scope note — what this guard does NOT cover:
/// - Controllers outside the Klacks.Api assembly, in particular Klacks.Plugin.Messaging.
/// - Whether the roles/policy on an endpoint are the RIGHT ones. Only the scheme is checked.
/// - Endpoints with no [Authorize] at all: an open endpoint is a different question and is not
///   flagged here.
/// - Minimal-API endpoints and anything routed outside MVC controllers.
/// </summary>

using System.Reflection;
using System.Text;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ControllerAuthorizeSchemeGuardTests
{
    private const string ControllerNamespaceFragment = "Presentation.Controllers";
    private const int MinimumControllers = 50;

    [Test]
    public void EveryAuthorizeAttributeOnAControllerMustResolveAnExplicitScheme()
    {
        var controllers = ScannedControllers();

        controllers.Count.ShouldBeGreaterThan(
            MinimumControllers,
            $"Only {controllers.Count} controllers were found under '{ControllerNamespaceFragment}'. " +
            "The guard cannot have inspected the real presentation layer, so a green result would be " +
            "meaningless.");

        controllers.Count(SchemeSuppliedByChain).ShouldBeGreaterThan(
            0,
            "No controller inherits a scheme-pinning class-level [Authorize]. Either BaseController " +
            "lost its attribute or the scan is not reading the real types — in both cases an empty " +
            "violation list proves nothing.");

        var violations = new List<string>();

        foreach (var controller in controllers)
        {
            if (SchemeSuppliedByChain(controller))
            {
                continue;
            }

            foreach (var attribute in controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            {
                if (string.IsNullOrWhiteSpace(attribute.AuthenticationSchemes))
                {
                    violations.Add($"{controller.FullName} (class level)");
                }
            }

            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                {
                    if (string.IsNullOrWhiteSpace(attribute.AuthenticationSchemes))
                    {
                        violations.Add($"{controller.FullName}.{method.Name}");
                    }
                }
            }
        }

        var report = new StringBuilder();
        foreach (var violation in violations.Distinct().OrderBy(v => v, StringComparer.Ordinal))
        {
            report.AppendLine($"  {violation}");
        }

        violations.ShouldBeEmpty(
            "An [Authorize] without a scheme resolves the runtime default, which AddIdentity overrides " +
            "to cookie authentication: the JWT caller is redirected to a login page instead of being " +
            "authorised. Either derive from BaseController (which pins the scheme for the whole type) " +
            "or write [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, ...)]." +
            $"{Environment.NewLine}{report}");
    }

    [Test]
    public void BaseController_IsTheSchemeSourceTheRestOfTheLayerRelliesOn()
    {
        var attribute = typeof(BaseController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .SingleOrDefault();

        attribute.ShouldNotBeNull(
            "BaseController carries the one class-level [Authorize] that supplies the JWT scheme to " +
            "every controller derived from it. Without it, every method-level [Authorize(Roles = ...)] " +
            "in the layer silently falls back to cookie authentication.");
        attribute!.AuthenticationSchemes.ShouldBe(
            Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
    }

    private static bool SchemeSuppliedByChain(Type controller)
    {
        var current = controller;
        while (current != null && current != typeof(object))
        {
            foreach (var attribute in current.GetCustomAttributes<AuthorizeAttribute>(inherit: false))
            {
                if (!string.IsNullOrWhiteSpace(attribute.AuthenticationSchemes))
                {
                    return true;
                }
            }

            current = current.BaseType;
        }

        return false;
    }

    private static List<Type> ScannedControllers()
    {
        return typeof(BaseController).Assembly
            .GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => (t.Namespace ?? string.Empty).Contains(ControllerNamespaceFragment, StringComparison.Ordinal))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
    }
}
