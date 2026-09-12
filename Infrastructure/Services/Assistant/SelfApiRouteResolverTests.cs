// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Verifies that the route a skill will call matches the route ASP.NET actually serves. The resolver
/// exists so the ~200 skills being converted never carry a hand-typed route; a wrong entry here would
/// turn into a 404 the model has to interpret, so every controller in the assembly that declares
/// ICrudResourceController is checked against its own attributes rather than a fixed expectation list.
/// </summary>

using System.Reflection;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class SelfApiRouteResolverTests
{
    private SelfApiRouteResolver _resolver = null!;

    [SetUp]
    public void SetUp() => _resolver = new SelfApiRouteResolver();

    public static IEnumerable<Type> GenericCrudControllers()
    {
        return typeof(InputBaseController<>).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && ResourceTypeOf(t) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
    }

    private static Type? ResourceTypeOf(Type type)
    {
        var marker = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICrudResourceController<>));

        return marker?.GetGenericArguments()[0];
    }

    [TestCaseSource(nameof(GenericCrudControllers))]
    public void EveryCrudController_IsReachableUnderItsOwnRoute(Type controller)
    {
        var resourceType = ResourceTypeOf(controller)!;

        var servedByOthers = GenericCrudControllers()
            .Count(other => ResourceTypeOf(other) == resourceType) > 1;
        if (servedByOthers)
        {
            // Several controllers on one resource cannot be told apart by type, and guessing would send
            // the write to the wrong endpoint — covered by AmbiguousResource_IsNotResolved.
            Assert.Pass($"{resourceType.Name} is served by more than one controller.");
        }

        _resolver.TryResolve(resourceType, out var route).ShouldBeTrue(
            $"{controller.Name} serves {resourceType.Name}, so a skill must be able to resolve its route.");

        // Rebuild the expectation from the attributes the same way routing does, so a renamed controller
        // or a changed route template fails here instead of at runtime.
        var template = RouteTemplateOf(controller);
        template.ShouldNotBeNull($"{controller.Name} has no [Route] anywhere in its hierarchy.");

        var expected = template!
            .Replace("[controller]", controller.Name.Replace("Controller", string.Empty), StringComparison.OrdinalIgnoreCase)
            .Trim('/');

        route.ShouldBe(expected);
    }

    private static string? RouteTemplateOf(Type controller)
    {
        for (var current = controller; current is not null; current = current.BaseType)
        {
            var template = current.GetCustomAttribute<RouteAttribute>(inherit: false)?.Template;
            if (!string.IsNullOrWhiteSpace(template))
            {
                return template;
            }
        }

        return null;
    }

    [Test]
    public void Resolve_MatchesTheHandWrittenRouteOfTheFirstConvertedSkill()
    {
        _resolver.Resolve(typeof(ExpensesResource)).ShouldBe(SelfApiRoutes.Expenses);
    }

    [Test]
    public void Resolve_ResourceWithoutAController_FailsLoudly()
    {
        var error = Should.Throw<InvalidOperationException>(() => _resolver.Resolve(typeof(string)));

        error.Message.ShouldContain("ICrudResourceController");
    }

    [Test]
    public void BuildMap_CoversEveryGenericCrudController()
    {
        var map = SelfApiRouteResolver.BuildMap(typeof(InputBaseController<>).Assembly);

        map.Sum(entry => entry.Value.Count).ShouldBe(GenericCrudControllers().Count());
    }

    [Test]
    public void AmbiguousResource_IsNotResolved()
    {
        var ambiguous = GenericCrudControllers()
            .Select(ResourceTypeOf)
            .GroupBy(type => type)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key!)
            .ToList();

        ambiguous.ShouldNotBeEmpty(
            "This test loses its meaning once no resource is served by two controllers — remove it then.");

        foreach (var resourceType in ambiguous)
        {
            _resolver.TryResolve(resourceType, out _).ShouldBeFalse(
                $"{resourceType.Name} is served by several controllers; picking one silently would send a " +
                "write to the wrong endpoint.");

            Should.Throw<InvalidOperationException>(() => _resolver.Resolve(resourceType))
                .Message.ShouldContain("more than one controller");
        }
    }
}
