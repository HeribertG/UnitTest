// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the gap between the Client entity and ClientResource. ClientRepository.Put copies a client
/// mapped from the resource over the stored one with entry.CurrentValues.SetValues(...), which writes
/// EVERY column — including the ones the resource cannot carry and the mapper therefore leaves at their
/// CLR default. Such a column is silently erased by an ordinary client save unless something protects
/// it. The incident this guards against: the LDAP link and the ERP customer reference pair were nulled
/// on every client PUT, unlinking a client from its identity provider and from its ERP customer record.
/// A column added to the entity later must therefore either gain a source in the resource, be preserved
/// by ClientRepository.ColumnsPreservedFromStoredClient, or be named here with the reason it survives
/// without either.
/// </summary>

using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using Klacks.Api.Application.DTOs.Staffs;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ClientWriteSurfaceGuardTests
{
    private const string ToEntityDeclaration = "Client ToEntity(ClientResource resource)";

    private static readonly Regex IgnoreTargetAttribute = new(
        @"^\[MapperIgnoreTarget\(nameof\(Client\.(?<name>[A-Za-z_][A-Za-z0-9_]*)\)\)\]$",
        RegexOptions.Compiled);

    /// <summary>
    /// Columns without a resource source that are nonetheless safe, each with the mechanism that keeps
    /// them safe. Every entry names a mechanism that is verifiable in the code, not an intention.
    /// </summary>
    private static readonly Dictionary<string, string> ColumnsSafeWithoutPreservation =
        new(StringComparer.Ordinal)
        {
            [nameof(Client.CreateTime)] =
                "DataBaseContext.ProtectCreationAuditFieldsFromUpdates sets AfterSaveBehavior.Ignore, so " +
                "EF omits the column from every UPDATE statement.",
            [nameof(Client.CurrentUserCreated)] =
                "DataBaseContext.ProtectCreationAuditFieldsFromUpdates sets AfterSaveBehavior.Ignore, so " +
                "EF omits the column from every UPDATE statement.",
            [nameof(Client.UpdateTime)] =
                "DataBaseContext.OnBeforeSaving rewrites it on every save; the copied null never reaches " +
                "the database.",
            [nameof(Client.CurrentUserUpdated)] =
                "DataBaseContext.OnBeforeSaving rewrites it on every save; the copied null never reaches " +
                "the database.",
            [nameof(Client.IsDeleted)] =
                "Soft-delete state, owned by DataBaseContext.OnBeforeSaving. The resource carries the " +
                "flag but the mapper ignores it as a target, which is what keeps a caller from deleting " +
                "a client through an update.",
            [nameof(Client.DeletedTime)] =
                "Soft-delete state, owned by DataBaseContext.OnBeforeSaving.",
            [nameof(Client.CurrentUserDeleted)] =
                "Soft-delete state, owned by DataBaseContext.OnBeforeSaving.",
            [nameof(Client.IdNumber)] =
                "ClientConfiguration pins Before- and AfterSaveBehavior to Ignore, so the database " +
                "sequence owns the column and neither insert nor update writes it.",
            [nameof(Client.PhoneticTokens)] =
                "DataBaseContext.ComputeClientPhoneticTokens recomputes it from the name fields whenever " +
                "one of them is modified, and ClientRepository.Put marks the whole entity modified, so " +
                "the copied null is overwritten before the save reaches the database."
        };

    [Test]
    public void EveryClientColumnWithoutAResourceSource_IsPreservedOrExplainedHere()
    {
        var unprotected = ColumnsWithoutResourceSource()
            .Where(name => !ClientRepository.ColumnsPreservedFromStoredClient.Contains(name, StringComparer.Ordinal))
            .Where(name => !ColumnsSafeWithoutPreservation.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        unprotected.ShouldBeEmpty(
            "These Client columns have no source in ClientResource, so a client mapped from that resource " +
            "leaves them at their CLR default and ClientRepository.Put writes that default over the " +
            "stored value on every ordinary save. Either map the column from the resource, add it to " +
            "ClientRepository.ColumnsPreservedFromStoredClient, or name it in " +
            $"{nameof(ColumnsSafeWithoutPreservation)} together with the mechanism that already protects " +
            "it. Unprotected: " + string.Join(", ", unprotected));
    }

    [Test]
    public void PreservedColumns_AreExactlyColumnsThatStillLackAResourceSource()
    {
        var withoutSource = ColumnsWithoutResourceSource().ToHashSet(StringComparer.Ordinal);

        var stale = ClientRepository.ColumnsPreservedFromStoredClient
            .Where(name => !withoutSource.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "These columns are preserved from the stored client although the resource now carries them, " +
            "so a caller can no longer change them and would not be told why. Remove them from " +
            "ClientRepository.ColumnsPreservedFromStoredClient: " + string.Join(", ", stale));
    }

    [Test]
    public void ExplainedColumns_AreStillColumnsWithoutAResourceSource()
    {
        var withoutSource = ColumnsWithoutResourceSource().ToHashSet(StringComparer.Ordinal);

        var stale = ColumnsSafeWithoutPreservation.Keys
            .Where(name => !withoutSource.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        stale.ShouldBeEmpty(
            "These justifications describe columns that are no longer unsourced — the column was mapped, " +
            "renamed or removed — so the text reads as a reviewed decision but guards nothing. Remove: "
            + string.Join(", ", stale));
    }

    /// <summary>
    /// Scalar Client columns the mapper cannot fill: either ClientResource has no property of that name,
    /// or ClientMapper.ToEntity ignores it as a target. Navigation properties and collections are out of
    /// scope because CurrentValues.SetValues copies scalars only.
    /// </summary>
    private static IEnumerable<string> ColumnsWithoutResourceSource()
    {
        var resourceProperties = typeof(ClientResource)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var ignoredByMapper = TargetsIgnoredByToEntity();

        return typeof(Client)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .Where(property => IsScalar(property.PropertyType))
            .Where(property => !resourceProperties.Contains(property.Name)
                               || ignoredByMapper.Contains(property.Name))
            .Select(property => property.Name);
    }

    /// <summary>
    /// Client properties that ClientMapper.ToEntity is told to leave alone. Read from the source rather
    /// than by reflection on purpose: Mapperly's attributes are compile-time only and are not present on
    /// the built assembly, so GetCustomAttributes returns an empty list and the guard would silently
    /// treat an ignored target as mapped — verified 2026-09-12, which is how this scan came to exist.
    /// </summary>
    private static HashSet<string> TargetsIgnoredByToEntity()
    {
        var source = File.ReadAllLines(LocateMapperSource());
        var declaration = Array.FindIndex(source, line => line.Contains(ToEntityDeclaration, StringComparison.Ordinal));

        declaration.ShouldBeGreaterThanOrEqualTo(
            0,
            $"'{ToEntityDeclaration}' was not found in ClientMapper.cs. The mapping method was renamed or " +
            "reshaped, so this guard is reading nothing and would be vacuously green.");

        var ignored = new HashSet<string>(StringComparer.Ordinal);
        for (var i = declaration - 1; i >= 0; i--)
        {
            var line = source[i].Trim();
            if (line.Length == 0)
            {
                break;
            }

            var match = IgnoreTargetAttribute.Match(line);
            if (!match.Success)
            {
                break;
            }

            ignored.Add(match.Groups["name"].Value);
        }

        ignored.ShouldNotBeEmpty(
            "No [MapperIgnoreTarget] was read above the ToEntity declaration. Either the attributes moved " +
            "or the scan no longer matches them, and the guard would understate the unsourced columns.");

        return ignored;
    }

    private static string LocateMapperSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "Klacks.Api", "Application", "Mappers", "ClientMapper.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate ClientMapper.cs above the test directory.");
    }

    private static bool IsScalar(Type type)
    {
        if (type == typeof(string))
        {
            return true;
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsValueType || underlying == typeof(string);
    }
}
