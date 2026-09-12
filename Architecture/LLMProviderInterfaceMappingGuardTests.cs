// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards a silent capability bug: ILLMProvider declares default interface implementations
/// (SupportsToolChoice, SupportsStreaming, GetEffectiveInputTokenLimit). A provider deriving from
/// BaseHttpProvider that merely re-declares such a member — instead of overriding a virtual on the
/// base class — does NOT re-map the interface, because only BaseHttpProvider names ILLMProvider in
/// its base list. Every call site holding an ILLMProvider then keeps reading the interface default
/// while the class-typed member says something else, and nothing warns. DeepSeek's tool_choice
/// support was invisible that way (llm_usage recorded toolChoiceSupported=false for every turn).
///
/// Covered: every ILLMProvider member of every concrete provider in the API assembly, in both shapes
/// the bug takes - a member that shadows an interface DEFAULT implementation, and one that shadows a
/// VIRTUAL of BaseHttpProvider with "new" instead of "override" (detected by comparing the declaring
/// type of the member found by reflection on the concrete type against the declaring type the
/// interface map points at). NOT covered: a negative case proving the guard actually fires - the
/// historical offender is fixed and no test double re-creates it, so the guard is only known to pass
/// on clean code, not known to fail on dirty code.
/// </summary>

using System.Reflection;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.DeepSeek;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class LLMProviderInterfaceMappingGuardTests
{
    private static IEnumerable<Type> ProviderTypes() =>
        typeof(Klacks.Api.Infrastructure.Services.Assistant.Providers.Base.BaseHttpProvider).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ILLMProvider).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

    // The interface-typed read is the one every consumer performs (LLMService holds an ILLMProvider).
    [Test]
    public void DeepSeek_ReportsToolChoiceSupport_ThroughTheInterface()
    {
        // Arrange
        ILLMProvider provider = new DeepSeekProvider(
            new HttpClient(),
            NullLogger<DeepSeekProvider>.Instance,
            Substitute.For<IConfiguration>());

        // Act & Assert
        provider.SupportsToolChoice.ShouldBeTrue();
    }

    [Test]
    public void NoProvider_ShadowsAnInterfaceMemberWithoutImplementingIt()
    {
        // Arrange
        var offenders = new List<string>();
        var membersImplementedOnAClass = 0;

        // Act
        foreach (var providerType in ProviderTypes())
        {
            var map = providerType.GetInterfaceMap(typeof(ILLMProvider));

            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                var interfaceMethod = map.InterfaceMethods[i];
                var target = map.TargetMethods[i];

                // What a caller holding the concrete class type would bind to. Null for an explicit
                // interface implementation, which is private and cannot shadow anything.
                var boundOnTheClass = providerType.GetMethod(
                    interfaceMethod.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                    binder: null,
                    types: interfaceMethod.GetParameters().Select(p => p.ParameterType).ToArray(),
                    modifiers: null);

                if (boundOnTheClass == null)
                {
                    continue;
                }

                var usesInterfaceDefault = target == null || target.DeclaringType == typeof(ILLMProvider);
                if (usesInterfaceDefault)
                {
                    offenders.Add($"{providerType.Name}.{interfaceMethod.Name} (shadows the interface default)");
                    continue;
                }

                membersImplementedOnAClass++;

                // An override and an untouched inherited member both agree with the interface map; only
                // a member re-declared with "new" on the provider binds to a different type than the one
                // an ILLMProvider-typed call lands on.
                if (boundOnTheClass.DeclaringType != target!.DeclaringType)
                {
                    offenders.Add(
                        $"{providerType.Name}.{interfaceMethod.Name} (hides {target.DeclaringType?.Name}, " +
                        "interface calls keep reaching the base member)");
                }
            }
        }

        // Assert
        // Without this the "hides a base virtual" branch could be dead code and the guard would pass
        // for the wrong reason: an empty scan is indistinguishable from a clean one.
        membersImplementedOnAClass.ShouldBeGreaterThan(
            0, "no ILLMProvider member is implemented on a class at all - the scan found nothing to check");
        offenders.ShouldBeEmpty(
            "these members are not what an ILLMProvider-typed caller reads - declare the member virtual " +
            "on BaseHttpProvider and override it: " + string.Join(", ", offenders));
    }
}
