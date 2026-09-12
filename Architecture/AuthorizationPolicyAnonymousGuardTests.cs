// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard over every named authorization policy of the application.
///
/// The trap: a policy named on [Authorize(Policy = ...)] REPLACES the default policy instead of adding
/// to it. AuthorizationPolicy.CombineAsync folds the default policy in only for an attribute that names
/// neither a policy nor roles, so a policy built from assertions alone is also evaluated for a request
/// whose authentication failed or never ran — with an empty ClaimsPrincipal. Since the Planer floor
/// (Option B, 2026-09-12) an empty principal expands to a non-empty rights list, which turned
/// "assert a permission" into "allow anonymous" for RequireAssistant and its three controllers.
///
/// Test 1: every constant of AuthorizationPolicies is actually registered and carries a
/// DenyAnonymousAuthorizationRequirement. A new policy that forgets RequireAuthenticatedUser, and a
/// constant that was never registered at all, both fail here.
/// Test 2: Program.cs registers through AddKlacksPolicies and nowhere else, so test 1 inspects the
/// policies the host really builds rather than a copy maintained in this file.
///
/// Scope note — what this guard does NOT cover:
/// - Whether the permission a policy asserts is the RIGHT one. Only anonymity is checked.
/// - A policy registered in Program.cs under a string literal instead of an AuthorizationPolicies
///   constant. Test 2 only refuses the constant-based form there, and test 1 only knows the constants,
///   so such a policy is invisible to both.
/// - Inline policies built at an endpoint instead of registered by name (Presentation/Mcp builds one);
///   that endpoint shares RequireAssistantAccess, which is covered by
///   AuthorizationPolicyBuilderExtensionsTests.
/// - Policies registered by plugins in their own assemblies.
/// </summary>

using System.Reflection;
using System.Text;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AuthorizationPolicyAnonymousGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string ProgramFileName = "Program.cs";
    private const string RegistrationCall = "AddKlacksPolicies";
    private const string DirectPolicyRegistration = "AddPolicy(AuthorizationPolicies.";

    [Test]
    public void EveryNamedPolicy_MustBeRegisteredAndDenyAnonymousCallers()
    {
        var options = new AuthorizationOptions().AddKlacksPolicies();
        var policyNames = DeclaredPolicyNames();

        policyNames.ShouldNotBeEmpty(
            "No policy name was read from AuthorizationPolicies, so the guard compared nothing and a " +
            "green result would be meaningless.");

        var violations = new List<string>();
        foreach (var name in policyNames)
        {
            var policy = options.GetPolicy(name);
            if (policy == null)
            {
                violations.Add($"'{name}' is declared in AuthorizationPolicies but never registered");
                continue;
            }

            if (!policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any())
            {
                violations.Add($"'{name}' has no DenyAnonymousAuthorizationRequirement");
            }
        }

        violations.ShouldBeEmpty(
            "A named policy replaces the default policy, so it is evaluated for an unauthenticated " +
            "request too — and Permissions.ExpandRoles([]) hands that empty principal the Planer floor. " +
            "Call RequireAuthenticatedUser() before the assertion, and register the policy in " +
            $"{nameof(AuthorizationOptionsExtensions)}.{RegistrationCall}." +
            $"{Environment.NewLine}{Format(violations)}");
    }

    [Test]
    public void ProgramCs_MustRegisterPoliciesThroughTheSharedExtension()
    {
        var source = File.ReadAllText(Path.Combine(LocateApiProject(), ProgramFileName));

        source.Contains(RegistrationCall, StringComparison.Ordinal).ShouldBeTrue(
            $"{ProgramFileName} no longer registers its authorization policies through " +
            $"{nameof(AuthorizationOptionsExtensions)}.{RegistrationCall}, so the guard above inspects a " +
            "set of policies the host does not build.");

        source.Contains(DirectPolicyRegistration, StringComparison.Ordinal).ShouldBeFalse(
            $"{ProgramFileName} registers a named policy directly. Move it into " +
            $"{nameof(AuthorizationOptionsExtensions)}.{RegistrationCall}, otherwise it is never checked " +
            "for the anonymous-caller trap.");
    }

    private static IReadOnlyList<string> DeclaredPolicyNames()
        => typeof(AuthorizationPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => f.GetRawConstantValue() as string)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

    private static string Format(IEnumerable<string> violations)
    {
        var report = new StringBuilder();
        foreach (var violation in violations)
        {
            report.AppendLine($"  {violation}");
        }

        return report.ToString();
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (File.Exists(Path.Combine(candidate, ProgramFileName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
