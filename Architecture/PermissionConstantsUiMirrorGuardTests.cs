// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Drift guard for the one rule of Option B spec 3.1: the Angular permission constants in
/// Klacks.Ui/src/app/domain/constants/permissions.constants.ts carry the SAME strings as
/// Klacks.Api Permissions plus Roles.Admin. Both sides are compared against the same literals the
/// backend enforces — a permission the UI knows but the backend does not is a guard that never
/// opens, and a permission the backend enforces but the UI does not know is a screen no route guard
/// can protect.
///
/// The check is a source scan of the TypeScript file rather than a build artefact, for the same
/// reason NavigationManifestPermissionGuardTests scans the generated manifests: the value is a
/// string literal in a file the backend build never compiles. Klacks.Ui is located as a sibling
/// directory of the Klacks.Api project.
///
/// Scope note — what this guard does NOT cover:
/// - Whether a given permission is used correctly in the Angular routes or templates. That is the
///   Klacks.Ui side (permission.guard.spec.ts, klacksy-page-keys-guard.spec.ts).
/// - Roles other than Admin. Authorised and User are not mirrored into the UI by spec 3.1.
/// - Values assembled at runtime (string concatenation, computed keys). Only plain literals of the
///   PERMISSIONS object and the ROLE_ADMIN constant are read.
/// </summary>

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PermissionConstantsUiMirrorGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string UiProjectDirectory = "Klacks.Ui";
    private const string RoleAdminConstantName = "ROLE_ADMIN";
    private const string PermissionsObjectName = "PERMISSIONS";

    private static readonly string[] ConstantsFileSegments =
    [
        "src", "app", "domain", "constants", "permissions.constants.ts"
    ];

    private static readonly Regex PermissionsObjectDeclaration = new(
        @"(?:export\s+)?const\s+PERMISSIONS\s*(?::[^=]+)?=\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex RoleAdminDeclaration = new(
        @"(?:export\s+)?const\s+ROLE_ADMIN\s*(?::[^=]+)?=\s*['""](?<value>[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex StringLiteral = new(
        @"['""](?<value>[^'""]+)['""]",
        RegexOptions.Compiled);

    [Test]
    public void UiPermissionConstants_MustMirrorTheBackendPermissionsAndAdminRole()
    {
        var path = ConstantsFilePath();

        File.Exists(path).ShouldBeTrue(
            $"The Angular permission constants file is missing: {path}. Option B spec 3.1 requires it; " +
            "until it exists the UI has no shared vocabulary with the backend and this guard cannot " +
            "compare anything — which is a failure, not a pass.");

        var source = File.ReadAllText(path);
        var uiValues = ReadPermissionLiterals(source);
        var roleAdmin = ReadRoleAdmin(source);

        roleAdmin.ShouldBe(
            Roles.Admin,
            $"{RoleAdminConstantName} in {UiProjectDirectory} must carry the backend Roles.Admin string; " +
            "the router guard and the Klacksy page keys compare against it literally.");

        uiValues.Count.ShouldBeGreaterThan(
            0,
            $"No string literal was read from the {PermissionsObjectName} object in {path}. The guard " +
            "would compare nothing, so a green result would be meaningless.");

        var backendValues = BackendPermissionConstants();

        var missingInUi = backendValues.Except(uiValues, StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        var unknownInBackend = uiValues.Except(backendValues, StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();

        var report = new StringBuilder();
        foreach (var value in missingInUi)
        {
            report.AppendLine($"  only in Klacks.Api Permissions: {value}");
        }

        foreach (var value in unknownInBackend)
        {
            report.AppendLine($"  only in {PermissionsObjectName} ({UiProjectDirectory}): {value}");
        }

        (missingInUi.Count + unknownInBackend.Count).ShouldBe(
            0,
            $"The {PermissionsObjectName} object must hold exactly the public const strings of " +
            "Klacks.Api Permissions. A value only the backend knows leaves a screen the UI cannot gate; " +
            "a value only the UI knows is a guard that never opens, because the backend enforces the " +
            $"literal.{Environment.NewLine}{report}");
    }

    private static IReadOnlySet<string> ReadPermissionLiterals(string source)
    {
        var match = PermissionsObjectDeclaration.Match(source);
        if (!match.Success)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var body = ExtractBracedBody(source, match.Index + match.Length - 1);
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match literal in StringLiteral.Matches(body))
        {
            values.Add(literal.Groups["value"].Value);
        }

        return values;
    }

    private static string? ReadRoleAdmin(string source)
    {
        var match = RoleAdminDeclaration.Match(source);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string ExtractBracedBody(string source, int openingBraceIndex)
    {
        var depth = 0;
        for (var i = openingBraceIndex; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source.Substring(openingBraceIndex + 1, i - openingBraceIndex - 1);
                }
            }
        }

        return string.Empty;
    }

    private static IReadOnlySet<string> BackendPermissionConstants()
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in typeof(Permissions).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string)
                && field.GetRawConstantValue() is string value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string ConstantsFilePath()
        => Path.Combine(LocateUiProject(), Path.Combine(ConstantsFileSegments));

    private static string LocateUiProject()
    {
        var apiProject = new DirectoryInfo(LocateApiProject());
        var repositoryRoot = apiProject.Parent
            ?? throw new DirectoryNotFoundException(
                $"The {ApiProjectDirectory} project has no parent directory, so {UiProjectDirectory} " +
                "cannot be located as its sibling.");

        return Path.Combine(repositoryRoot.FullName, UiProjectDirectory);
    }

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
}
