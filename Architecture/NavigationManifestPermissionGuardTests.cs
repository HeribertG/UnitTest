// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard over the two scanner-generated navigation manifests in Klacks.Api
/// (Application/Skills/Definitions/navigation-targets.json and klacksy-page-keys.generated.json).
/// Both carry a requiredPermission that the backend enforces — NavigateToSkill for the LLM path,
/// NavigationTargetMatcher for the chat fast-path — so a value that matches no constant silently
/// locks a destination out for everyone but Admin (who bypasses), and an in-page target without the
/// permission of its own page lets the assistant offer a destination the router guard then refuses.
///
/// Test 1: every requiredPermission value — and every actionPermission of a page key — is a public
/// const string of Roles or Permissions. Comma-separated lists are checked element by element
/// (Permissions.HasAllRequiredPermissions splits them the same way). The actionPermission half stays
/// vacuous until the Klacks.Ui scanner emits the field (spec 2.4); it is deliberately not backed by a
/// minimum count, so this suite does not go red on the scanner's timeline.
/// Test 2: page keys sharing a route share a permission (spec 3.1) — the precondition without which
/// no single permission can be inherited by that route's in-page targets. Routes that violate it are
/// excluded from test 3, so the two failures never mask each other.
/// Test 3: every non-obsolete in-page target whose route has a page key with a permission carries
/// exactly that permission — the drift guard for the Klacks.Ui scanner rule in spec section 3.2
/// ("in-page targets inherit the page permission"). Inheritance is from requiredPermission, the
/// permission of the ROUTE, never from actionPermission: an action right belongs to the one page key
/// that performs the action, while a route's in-page targets are shared by every page key on it. The
/// page-category targets that carry actionPermission instead (scanner rule of spec 2.4:
/// requiredPermission = actionPermission ?? requiredPermission, so the chat fast-path applies the same
/// gate NavigateToSkill applies) are skipped here, as they always were.
/// Test 4: the same drift guard for requiredFeature. A feature-gated page exists only where the
/// installation has the feature, and the chat fast-path drops the targets of a missing feature from
/// its snapshot on the manifest value alone — so a target left without the feature of its page stays
/// offered on installations that do not have the page at all, and the router bounces the click.
///
/// Scope note — what this guard does NOT cover:
/// - Whether a permission is the RIGHT one for a page. That is the Angular-guard mirror rule of
///   spec 3.1 and is checked on the Klacks.Ui side (klacksy-page-keys-guard.spec.ts).
/// - Page-category targets in navigation-targets.json. They are generated from the page-key file,
///   so checking them against it would only restate test 1.
/// - Routes carrying an entity id (/x/:id). Targets are scanned per template, never per instance.
/// - Routes for which no page key exists at all (plugin routes, the global shell "/"): nothing to
///   inherit from, so nothing to compare.
/// </summary>

using System.Reflection;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class NavigationManifestPermissionGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string PageCategory = "page";
    private const int MinimumPageKeys = 20;
    private const int MinimumTargets = 100;
    private const int MinimumFeatureGatedPageKeys = 1;

    private static readonly string[] ManifestPathSegments = ["Application", "Skills", "Definitions"];
    private const string TargetManifestFileName = "navigation-targets.json";
    private const string PageKeyManifestFileName = "klacksy-page-keys.generated.json";

    [Test]
    public void EveryRequiredPermissionValue_MustBeARolesOrPermissionsConstant()
    {
        var known = KnownPermissionConstants();
        var pageKeys = LoadPageKeys();
        var targets = LoadTargets();

        pageKeys.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumPageKeys,
            $"Only {pageKeys.Count} page keys were read from {PageKeyManifestFileName}. The guard cannot " +
            "have inspected the real manifest, so a green result would be meaningless.");
        targets.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumTargets,
            $"Only {targets.Count} targets were read from {TargetManifestFileName}. The guard cannot " +
            "have inspected the real manifest, so a green result would be meaningless.");

        var violations = new List<string>();
        foreach (var pageKey in pageKeys)
        {
            violations.AddRange(UnknownValues(pageKey.RequiredPermission, known)
                .Select(value => $"{PageKeyManifestFileName}: page key '{pageKey.PageKey}' requires '{value}'"));
            violations.AddRange(UnknownValues(pageKey.ActionPermission, known)
                .Select(value => $"{PageKeyManifestFileName}: page key '{pageKey.PageKey}' needs the action right '{value}'"));
        }

        foreach (var target in targets)
        {
            violations.AddRange(UnknownValues(target.RequiredPermission, known)
                .Select(value => $"{TargetManifestFileName}: target '{target.TargetId}' requires '{value}'"));
        }

        violations.ShouldBeEmpty(
            "Every requiredPermission must be a public const string of Roles or Permissions — a free " +
            "string is enforced literally and locks the destination out for every non-Admin caller. " +
            $"Known constants: {string.Join(", ", known.OrderBy(v => v, StringComparer.Ordinal))}." +
            $"{Environment.NewLine}{Format(violations)}");
    }

    [Test]
    public void PageKeysSharingARoute_MustShareTheirPermission()
    {
        var pageKeys = LoadPageKeys();
        pageKeys.Count.ShouldBeGreaterThanOrEqualTo(MinimumPageKeys);

        var (_, divergentRoutes) = PagePermissionByRoute(pageKeys);

        divergentRoutes.ShouldBeEmpty(
            "Page keys sharing a route must share a permission (spec 3.1). The router guards one " +
            "route, not one page key, so two permissions on the same route mean at least one of them " +
            "is wrong — and the in-page targets of that route have no single permission to inherit. " +
            $"Those routes are excluded from the inheritance check below until this is fixed." +
            $"{Environment.NewLine}{Format(divergentRoutes.Select(r => $"route '{r}' has page keys with different permissions"))}");
    }

    [Test]
    public void EveryInPageTarget_MustCarryThePermissionOfItsPage()
    {
        var pageKeys = LoadPageKeys();
        var targets = LoadTargets();

        pageKeys.Count.ShouldBeGreaterThanOrEqualTo(MinimumPageKeys);
        targets.Count.ShouldBeGreaterThanOrEqualTo(MinimumTargets);

        var (permissionByRoute, _) = PagePermissionByRoute(pageKeys);

        var violations = new List<string>();
        foreach (var target in targets)
        {
            if (target.Obsolete || IsPageCategory(target.Category))
            {
                continue;
            }

            if (!permissionByRoute.TryGetValue(NormalizeRoute(target.Route), out var expected) || expected == null)
            {
                continue;
            }

            if (!string.Equals(target.RequiredPermission, expected, StringComparison.Ordinal))
            {
                violations.Add(
                    $"target '{target.TargetId}' (route {target.Route}) has " +
                    $"'{target.RequiredPermission ?? "null"}' but its page requires '{expected}'");
            }
        }

        violations.ShouldBeEmpty(
            "In-page targets inherit the permission of their page (spec 3.2): the chat fast-path " +
            "filters on the manifest value alone, so a target left at null offers an Admin-only " +
            "section to every user, who is then bounced by the Angular guard. Re-run the Klacks.Ui " +
            $"scanner (npm run scan:targets) after the inheritance rule is in place." +
            $"{Environment.NewLine}{Format(violations)}");
    }

    [Test]
    public void EveryInPageTarget_MustCarryTheFeatureOfItsPage()
    {
        var pageKeys = LoadPageKeys();
        var targets = LoadTargets();

        pageKeys.Count.ShouldBeGreaterThanOrEqualTo(MinimumPageKeys);
        targets.Count.ShouldBeGreaterThanOrEqualTo(MinimumTargets);

        pageKeys.Count(p => !string.IsNullOrWhiteSpace(p.RequiredFeature)).ShouldBeGreaterThanOrEqualTo(
            MinimumFeatureGatedPageKeys,
            $"No page key in {PageKeyManifestFileName} carries a requiredFeature, so the inheritance check " +
            "below compares nothing and a green result would be meaningless. The field exists in " +
            "klacksy-page-keys.ts; re-run the Klacks.Ui scanner (npm run scan:targets) to carry it into " +
            "the manifests.");

        var (featureByRoute, divergentRoutes) = PageFeatureByRoute(pageKeys);

        divergentRoutes.ShouldBeEmpty(
            "Page keys sharing a route must name the same requiredFeature: one route is gated by one " +
            $"guard, so its in-page targets have no single feature to inherit.{Environment.NewLine}" +
            Format(divergentRoutes.Select(r => $"route '{r}' has page keys with different features")));

        var violations = new List<string>();
        foreach (var target in targets)
        {
            if (target.Obsolete || IsPageCategory(target.Category))
            {
                continue;
            }

            if (!featureByRoute.TryGetValue(NormalizeRoute(target.Route), out var expected) || expected == null)
            {
                continue;
            }

            if (!string.Equals(target.RequiredFeature, expected, StringComparison.Ordinal))
            {
                violations.Add(
                    $"target '{target.TargetId}' (route {target.Route}) has " +
                    $"'{target.RequiredFeature ?? "null"}' but its page requires the feature '{expected}'");
            }
        }

        violations.ShouldBeEmpty(
            "In-page targets inherit the requiredFeature of their page: the chat fast-path filters the " +
            "snapshot on the manifest value alone, so a target left at null is offered on installations " +
            "that do not have the feature, and the router guard then bounces the click. Re-run the " +
            $"Klacks.Ui scanner (npm run scan:targets) after the inheritance rule is in place." +
            $"{Environment.NewLine}{Format(violations)}");
    }

    private static (Dictionary<string, string?> FeatureByRoute, List<string> DivergentRoutes) PageFeatureByRoute(
        IReadOnlyList<PageKeyRecord> pageKeys)
    {
        var featureByRoute = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var divergentRoutes = new List<string>();

        foreach (var pageKey in pageKeys)
        {
            var route = NormalizeRoute(pageKey.Route);
            if (route.Length == 0)
            {
                continue;
            }

            if (!featureByRoute.TryGetValue(route, out var existing))
            {
                featureByRoute[route] = pageKey.RequiredFeature;
                continue;
            }

            if (!string.Equals(existing, pageKey.RequiredFeature, StringComparison.Ordinal)
                && !divergentRoutes.Contains(route, StringComparer.OrdinalIgnoreCase))
            {
                divergentRoutes.Add(route);
            }
        }

        foreach (var route in divergentRoutes)
        {
            featureByRoute.Remove(route);
        }

        return (featureByRoute, divergentRoutes);
    }

    private static IEnumerable<string> UnknownValues(string? requiredPermission, IReadOnlySet<string> known)
    {
        if (string.IsNullOrWhiteSpace(requiredPermission))
        {
            yield break;
        }

        foreach (var value in requiredPermission.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!known.Contains(value))
            {
                yield return value;
            }
        }
    }

    private static (Dictionary<string, string?> PermissionByRoute, List<string> DivergentRoutes) PagePermissionByRoute(
        IReadOnlyList<PageKeyRecord> pageKeys)
    {
        var permissionByRoute = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var divergentRoutes = new List<string>();

        foreach (var pageKey in pageKeys)
        {
            var route = NormalizeRoute(pageKey.Route);
            if (route.Length == 0)
            {
                continue;
            }

            if (!permissionByRoute.TryGetValue(route, out var existing))
            {
                permissionByRoute[route] = pageKey.RequiredPermission;
                continue;
            }

            if (!string.Equals(existing, pageKey.RequiredPermission, StringComparison.Ordinal)
                && !divergentRoutes.Contains(route, StringComparer.OrdinalIgnoreCase))
            {
                divergentRoutes.Add(route);
            }
        }

        foreach (var route in divergentRoutes)
        {
            permissionByRoute.Remove(route);
        }

        return (permissionByRoute, divergentRoutes);
    }

    private static bool IsPageCategory(string? category)
        => string.Equals(category, PageCategory, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return string.Empty;
        }

        var withoutQuery = route.Split('?', 2)[0].Trim();
        return withoutQuery.Length > 1 ? withoutQuery.TrimEnd('/') : withoutQuery;
    }

    private static IReadOnlySet<string> KnownPermissionConstants()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in new[] { typeof(Roles), typeof(Permissions) })
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string)
                    && field.GetRawConstantValue() is string value)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static IReadOnlyList<PageKeyRecord> LoadPageKeys()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath(PageKeyManifestFileName)));
        var entries = document.RootElement.GetProperty("entries");
        return entries.EnumerateArray()
            .Select(e => new PageKeyRecord(
                ReadString(e, "pageKey") ?? string.Empty,
                ReadString(e, "route") ?? string.Empty,
                ReadString(e, "requiredPermission"),
                ReadString(e, "requiredFeature"),
                ReadString(e, "actionPermission")))
            .ToList();
    }

    private static IReadOnlyList<TargetRecord> LoadTargets()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath(TargetManifestFileName)));
        return document.RootElement.EnumerateArray()
            .Select(e => new TargetRecord(
                ReadString(e, "targetId") ?? string.Empty,
                ReadString(e, "route") ?? string.Empty,
                ReadString(e, "category"),
                ReadString(e, "requiredPermission"),
                ReadString(e, "requiredFeature"),
                e.TryGetProperty("obsolete", out var obsolete) && obsolete.ValueKind == JsonValueKind.True))
            .ToList();
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Format(IEnumerable<string> violations)
    {
        var report = new StringBuilder();
        foreach (var violation in violations)
        {
            report.AppendLine($"  {violation}");
        }

        return report.ToString();
    }

    private static string ManifestPath(string fileName)
        => Path.Combine(LocateApiProject(), Path.Combine(ManifestPathSegments), fileName);

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, "Domain", "Services")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }

    private sealed record PageKeyRecord(
        string PageKey,
        string Route,
        string? RequiredPermission,
        string? RequiredFeature,
        string? ActionPermission = null);

    private sealed record TargetRecord(
        string TargetId,
        string Route,
        string? Category,
        string? RequiredPermission,
        string? RequiredFeature,
        bool Obsolete);
}
